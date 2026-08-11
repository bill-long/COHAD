import { Component, OnInit } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
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
  /**
   * The clear endpoint's provider warning: the suppression is cleared here, but reactivating the
   * address at the email provider failed, so its mail may still be silently dropped. Distinct
   * from errorText because the requested action DID succeed locally, and from noticeText because
   * it needs warning styling - it describes a condition the admin must act on, not guidance.
   */
  warningText: string | null = null;
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
  ) {}

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading = true;
    this.errorText = null;
    // Also clear the 409 "try again" notice: a fresh load (including a checkbox toggle) supersedes
    // it, so leaving it would falsely imply the reload itself was contended. The provider warning
    // goes too - a user-initiated reload is the next action, and clear() re-sets it AFTER calling
    // load() when the warning must outlive the reload it triggers.
    this.noticeText = null;
    this.warningText = null;
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

  clear(suppression: EmailSuppression): void {
    if (this.clearingIds.has(suppression.id)) {
      return;
    }
    this.clearingIds.add(suppression.id);
    this.errorText = null;
    this.noticeText = null;
    this.suppressionService.clearSuppression(suppression.id).subscribe({
      next: result => {
        this.clearingIds.delete(suppression.id);
        this.suppressedAddresses.refresh();
        this.load();
        // After load(), which resets the banners: this warning is the one piece of the clear
        // outcome that must survive its own reload ("cleared here, but the address may still be
        // suppressed at the provider").
        this.warningText = result.providerWarning;
      },
      error: err => {
        this.applyWriteError(err, 'Failed to clear the suppression.');
        this.clearingIds.delete(suppression.id);
      },
    });
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
