import { Component, OnInit } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { MatDialog } from '@angular/material/dialog';
import { ConfirmDialogComponent, ConfirmDialogData } from '../confirm-dialog/confirm-dialog.component';
import { EmailSuppression, SuppressionReason } from 'src/app/models';
import { EmailSuppressionService } from 'src/app/services/email-suppression.service';
import { SuppressedAddressesService } from 'src/app/services/suppressed-addresses.service';
import { httpErrorMessage } from 'src/app/utils/http-error-message';
import { suppressionReasonLabel } from 'src/app/utils/suppression-reason-label';

/**
 * Administrator surface over the email suppression list: which addresses receive no mail at all,
 * why, and since when - plus suppress-by-hand (the "resident phoned the board" path) and clear.
 * Create and clear return 409 on write contention (racing another admin or a webhook); that is
 * surfaced as "try again", not as an error state.
 */
@Component({
  selector: 'app-manage-suppressions',
  templateUrl: './manage-suppressions.component.html',
  styleUrls: ['./manage-suppressions.component.css'],
  standalone: false,
})
export class ManageSuppressionsComponent implements OnInit {
  suppressions: EmailSuppression[] = [];
  loading = true;
  errorText: string | null = null;
  /** Non-error guidance, e.g. the 409 "try again" message. */
  noticeText: string | null = null;
  includeCleared = false;
  newEmail = '';
  createInProgress = false;
  /** Ids with a clear in flight, so a double-click cannot double-clear (and rows disable independently). */
  clearingIds = new Set<string>();

  /**
   * Monotonic token guarding against out-of-order load responses: a fast checkbox toggle (or a
   * create/clear reload racing a toggle) can leave two getSuppressions calls in flight, and only
   * the most recently issued one may apply its response - otherwise the table can show cleared rows
   * while the checkbox is unchecked (or the reverse) until a manual reload.
   */
  private loadGeneration = 0;

  constructor(
    private readonly suppressionService: EmailSuppressionService,
    private readonly suppressedAddresses: SuppressedAddressesService,
    private readonly dialog: MatDialog,
  ) {}

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading = true;
    this.errorText = null;
    // Also clear the 409 "try again" notice: a fresh load (including a checkbox toggle) supersedes
    // it, so leaving it would falsely imply the reload itself was contended.
    this.noticeText = null;
    const generation = ++this.loadGeneration;
    this.suppressionService.getSuppressions(this.includeCleared).subscribe({
      next: suppressions => {
        if (generation !== this.loadGeneration) return;
        this.suppressions = suppressions;
        this.loading = false;
      },
      error: err => {
        if (generation !== this.loadGeneration) return;
        this.errorText = httpErrorMessage(err, 'Failed to load the suppression list.');
        this.loading = false;
      },
    });
  }

  onIncludeClearedChange(includeCleared: boolean): void {
    this.includeCleared = includeCleared;
    this.load();
  }

  get canCreate(): boolean {
    return !this.createInProgress && this.newEmail.trim().includes('@');
  }

  create(): void {
    if (!this.canCreate) {
      return;
    }
    this.createInProgress = true;
    this.errorText = null;
    this.noticeText = null;
    this.suppressionService.createSuppression(this.newEmail.trim()).subscribe({
      next: () => {
        this.newEmail = '';
        this.createInProgress = false;
        // Keep the read-only chips in the address editors in step with this change.
        this.suppressedAddresses.refresh();
        this.load();
      },
      error: err => {
        this.applyWriteError(err, 'Failed to suppress the address.');
        this.createInProgress = false;
      },
    });
  }

  /**
   * Confirmation first, restating the reason-specific caution: the help doc's "only clear when
   * you understand why" guidance has to reach the admin at the moment of the click, not live
   * only behind the help drawer - and a provider-unsubscribe clear changes provider-side state
   * COHAD cannot restore by itself.
   */
  clear(suppression: EmailSuppression): void {
    if (this.clearingIds.has(suppression.id)) {
      return;
    }
    const data: ConfirmDialogData = {
      title: 'Clear this suppression?',
      body: this.clearConfirmationBody(suppression),
      confirmText: 'Clear',
      cancelText: 'Cancel',
      confirmColor: 'warn',
    };
    this.dialog
      .open(ConfirmDialogComponent, { data })
      .afterClosed()
      .subscribe(confirmed => {
        if (confirmed === true) {
          this.performClear(suppression);
        }
      });
  }

  private performClear(suppression: EmailSuppression): void {
    // Re-checked here (not only in clear()): the dialog leaves a gap between click and
    // confirmation in which another path could have started a clear for the same row.
    if (this.clearingIds.has(suppression.id)) {
      return;
    }
    this.clearingIds.add(suppression.id);
    this.errorText = null;
    this.noticeText = null;
    this.suppressionService.clearSuppression(suppression.id, suppression.suppressedUtc).subscribe({
      next: () => {
        this.clearingIds.delete(suppression.id);
        this.suppressedAddresses.refresh();
        this.load();
      },
      error: err => {
        this.applyWriteError(err, 'Failed to clear the suppression.');
        this.clearingIds.delete(suppression.id);
      },
    });
  }

  /**
   * The lead sentence is the general rule from the help doc; the second paragraph is the
   * reason-specific stake - what this particular clear risks or requires.
   */
  private clearConfirmationBody(suppression: EmailSuppression): string {
    const lead =
      `Mail to ${suppression.email} resumes, subject to its opt-in preferences. ` +
      'Only clear a suppression when you understand why the address was suppressed and believe it is resolved.';
    switch (suppression.reason) {
      case 'HardBounce':
        return (
          `${lead}\n\nThis address hard-bounced (its provider reported it undeliverable). Clear only ` +
          'if the resident has confirmed the mailbox works again. If Postmark also suppressed the ' +
          'address on its side, reactivate it in the Postmark dashboard too, or the daily sync will ' +
          're-add this suppression.'
        );
      case 'SpamComplaint':
        return (
          `${lead}\n\nThe recipient reported association mail as spam. Clearing without their ` +
          "agreement risks the association's sending reputation."
        );
      case 'ResidentRequest':
        return `${lead}\n\nThe resident asked for all association mail to stop. Clear only at their request.`;
      case 'AdminAction':
        return (
          `${lead}\n\nAn administrator suppressed this address by hand - check the audit log if you ` +
          'do not know why.'
        );
      case 'ProviderUnsubscribe':
        return (
          `${lead}\n\nThe recipient unsubscribed through the email provider. Clearing also ` +
          "reactivates the address on the provider's suppression lists, which COHAD cannot undo by " +
          'itself - do this only at the resident\'s request.'
        );
      default:
        return lead;
    }
  }

  reasonLabel(reason: SuppressionReason): string {
    return suppressionReasonLabel(reason);
  }

  /**
   * The list shows the address itself; SuppressedBy/ClearedBy are provenance strings
   * (system:delivery-event, a credential type, or a user id) rendered verbatim.
   */
  private applyWriteError(err: unknown, fallback: string): void {
    if (err instanceof HttpErrorResponse && err.status === 409) {
      // Expected contention (another admin, or a webhook writing the same record) - the write was
      // simply not applied, so this is guidance rather than an error state.
      this.noticeText = 'The record was updated by someone else at the same time. Please try again.';
      return;
    }
    this.errorText = httpErrorMessage(err, fallback);
  }
}
