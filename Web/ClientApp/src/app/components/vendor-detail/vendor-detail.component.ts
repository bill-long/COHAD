import { Component, Inject, OnDestroy, OnInit, ViewChild } from '@angular/core';
import { MatExpansionPanel } from '@angular/material/expansion';
import { ActivatedRoute, Router } from '@angular/router';
import { FormBuilder } from '@angular/forms';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Observable, Subscription } from 'rxjs';
import { VendorDetail, VendorFlag, VendorReview, VendorsService, vendorCategoryClass } from 'src/app/services/vendors.service';
import { ApplicationState, applicationState } from 'src/app/state';
import { VendorFlagNotificationsService } from 'src/app/services/vendor-flag-notifications.service';
import { ConfirmDialogComponent } from '../confirm-dialog/confirm-dialog.component';
import { VendorEditorDialogComponent, VendorEditorDialogData } from '../vendor-editor-dialog/vendor-editor-dialog.component';

@Component({
  selector: 'app-vendor-detail',
  templateUrl: './vendor-detail.component.html',
  styleUrls: ['./vendor-detail.component.css'],
  standalone: false
})
export class VendorDetailComponent implements OnInit, OnDestroy {
  @ViewChild('reviewPanel') reviewPanel?: MatExpansionPanel;

  loading = false;
  saving = false;
  error: string | null = null;
  vendor: VendorDetail | null = null;
  editingReviewId: string | null = null;
  submittingFlag = false;
  flagError: string | null = null;

  readonly reviewForm = this.formBuilder.group({
    reviewText: ['']
  });

  readonly editReviewForm = this.formBuilder.group({
    reviewText: ['']
  });

  readonly flagForm = this.formBuilder.group({
    flagNote: ['']
  });

  private static readonly minNewReviewLength = 10;
  private static readonly minFlagNoteLength = 10;

  private currentUserUniqueId: string | null = null;
  private appStateSub?: Subscription;

  constructor(
    private readonly route: ActivatedRoute,
    private readonly router: Router,
    private readonly formBuilder: FormBuilder,
    private readonly vendorsService: VendorsService,
    private readonly vendorFlagNotificationsService: VendorFlagNotificationsService,
    private readonly dialog: MatDialog,
    private readonly snackBar: MatSnackBar,
    @Inject(applicationState) private readonly appState: Observable<ApplicationState>) { }

  ngOnInit(): void {
    this.appStateSub = this.appState.subscribe(s => {
      this.currentUserUniqueId = s.apiUser?.uniqueId ?? null;
    });

    const id = this.route.snapshot.paramMap.get('id');
    if (!id) {
      this.error = 'Vendor not found.';
      return;
    }

    this.load(id);
  }

  categoryClass = vendorCategoryClass;

  /** Safe href for vendor website (adds https:// when missing). */
  websiteUrl(raw: string | null | undefined): string {
    const s = (raw ?? '').trim();
    if (!s) {
      return '';
    }
    if (/^https?:\/\//i.test(s)) {
      return s;
    }
    return `https://${s}`;
  }

  /** True when the new-review field has at least `minNewReviewLength` non-whitespace characters. */
  get canSubmitNewReview(): boolean {
    const t = (this.reviewForm.controls.reviewText.value ?? '').trim();
    return t.length >= VendorDetailComponent.minNewReviewLength;
  }

  /** True for admins (API sets pendingFlags to a non-null array for admins, null for residents). */
  get isAdmin(): boolean {
    return this.vendor?.pendingFlags !== null && this.vendor?.pendingFlags !== undefined;
  }

  /** Matches API rules: administrator or vendor creator may edit/delete the listing. */
  get canManageVendor(): boolean {
    if (!this.vendor) {
      return false;
    }
    if (this.isAdmin) {
      return true;
    }
    const owner = (this.vendor.createdByUniqueId ?? '').trim().toLowerCase();
    const me = (this.currentUserUniqueId ?? '').trim().toLowerCase();
    return owner.length > 0 && me.length > 0 && owner === me;
  }

  /** True when this resident already has a pending (unresolved) flag on this vendor. */
  get hasPendingFlag(): boolean {
    return this.vendor?.myFlag?.status === 'Pending';
  }

  /** True when the flag note field meets the minimum length requirement. */
  get canSubmitFlag(): boolean {
    return (this.flagForm.controls.flagNote.value ?? '').trim().length >= VendorDetailComponent.minFlagNoteLength;
  }

  /** Imported / legacy reviews may have unknown modified time (API flag or year 0001). */
  isUnknownReviewModified(review: VendorReview): boolean {
    if (review.modifiedUtcIsUnknown === true) {
      return true;
    }
    if (review.modifiedUtcIsUnknown === false) {
      return false;
    }
    const d = new Date(review.modifiedUtc);
    return !Number.isNaN(d.getTime()) && d.getUTCFullYear() <= 1;
  }

  private load(id: string): void {
    this.loading = true;
    this.error = null;
    this.vendorsService.getVendor(id).subscribe({
      next: (vendor) => {
        this.vendor = vendor;
        this.loading = false;
      },
      error: () => {
        this.error = 'Unable to load vendor.';
        this.loading = false;
      }
    });
  }

  submitReview(): void {
    if (!this.vendor) {
      return;
    }

    const reviewText = (this.reviewForm.controls.reviewText.value ?? '').trim();
    if (!reviewText) {
      this.error = 'Review text is required.';
      return;
    }
    if (reviewText.length < VendorDetailComponent.minNewReviewLength) {
      this.error = `Review must be at least ${VendorDetailComponent.minNewReviewLength} characters.`;
      return;
    }

    this.saving = true;
    this.error = null;
    this.vendorsService.addReview(this.vendor.id, {
      reviewText
    }).subscribe({
      next: () => {
        this.reviewForm.reset({ reviewText: '' });
        this.reviewPanel?.close();
        this.saving = false;
        this.load(this.vendor!.id);
      },
      error: () => {
        this.error = 'Unable to save review.';
        this.saving = false;
      }
    });
  }

  startEdit(review: VendorReview): void {
    this.editingReviewId = review.id;
    this.editReviewForm.setValue({ reviewText: review.reviewText ?? '' });
  }

  cancelEdit(): void {
    this.editingReviewId = null;
    this.editReviewForm.reset({ reviewText: '' });
  }

  saveEdit(review: VendorReview): void {
    if (!this.vendor) {
      return;
    }

    const reviewText = (this.editReviewForm.controls.reviewText.value ?? '').trim();
    if (!reviewText) {
      this.error = 'Review text is required.';
      return;
    }

    this.saving = true;
    this.error = null;
    this.vendorsService.updateReview(this.vendor.id, review.id, { reviewText }).subscribe({
      next: () => {
        this.saving = false;
        this.cancelEdit();
        this.load(this.vendor!.id);
      },
      error: () => {
        this.error = 'Unable to update review.';
        this.saving = false;
      }
    });
  }

  deleteReview(review: VendorReview): void {
    if (!this.vendor) {
      return;
    }

    const ref = this.dialog.open(ConfirmDialogComponent, {
      data: {
        title: 'Delete review?',
        body: 'This review will be permanently deleted. This action cannot be undone.',
        confirmText: 'Delete',
        cancelText: 'Cancel',
        confirmColor: 'warn'
      }
    });

    ref.afterClosed().subscribe(confirmed => {
      if (confirmed !== true) {
        return;
      }

      this.saving = true;
      this.error = null;
      this.vendorsService.deleteReview(this.vendor!.id, review.id).subscribe({
        next: () => {
          this.saving = false;
          this.load(this.vendor!.id);
        },
        error: () => {
          this.error = 'Unable to delete review.';
          this.saving = false;
        }
      });
    });
  }

  submitFlag(): void {
    if (!this.vendor || this.hasPendingFlag) {
      return;
    }

    const flagNote = (this.flagForm.controls.flagNote.value ?? '').trim();
    if (flagNote.length < VendorDetailComponent.minFlagNoteLength) {
      return;
    }

    this.submittingFlag = true;
    this.flagError = null;
    this.vendorsService.submitFlag(this.vendor.id, { flagNote }).subscribe({
      next: () => {
        this.flagForm.reset({ flagNote: '' });
        this.submittingFlag = false;
        this.load(this.vendor!.id);
      },
      error: () => {
        this.flagError = 'Unable to submit report. Please try again.';
        this.submittingFlag = false;
      }
    });
  }

  dismissFlag(flag: VendorFlag): void {
    if (!this.vendor) {
      return;
    }

    const ref = this.dialog.open(ConfirmDialogComponent, {
      data: {
        title: 'Dismiss report?',
        body: 'This will mark the report as resolved. The resident will see that it has been addressed.',
        confirmText: 'Dismiss',
        cancelText: 'Cancel',
        confirmColor: 'warn'
      }
    });

    ref.afterClosed().subscribe(confirmed => {
      if (confirmed !== true) {
        return;
      }

      this.saving = true;
      this.error = null;
      this.vendorsService.dismissFlag(this.vendor!.id, flag.id).subscribe({
        next: () => {
          this.vendorFlagNotificationsService.removeNotification(flag.id);
          this.saving = false;
          this.load(this.vendor!.id);
        },
        error: () => {
          this.error = 'Unable to dismiss report.';
          this.saving = false;
        }
      });
    });
  }

  editVendor(): void {
    if (!this.vendor || !this.canManageVendor) {
      return;
    }

    const ref = this.dialog.open(VendorEditorDialogComponent, {
      data: { vendor: this.vendor } as VendorEditorDialogData,
      width: '720px',
      maxWidth: 'calc(100vw - 32px)',
      maxHeight: '90vh'
    });

    ref.afterClosed().subscribe(updated => {
      if (!updated) {
        return;
      }

      this.vendor = updated;
      this.snackBar.open('Vendor updated.', '', { duration: 4000 });
    });
  }

  deleteVendor(): void {
    if (!this.vendor || !this.canManageVendor) {
      return;
    }

    const ref = this.dialog.open(ConfirmDialogComponent, {
      data: {
        title: 'Delete vendor?',
        body: 'This vendor and all associated reviews/reports will be permanently deleted. This action cannot be undone.',
        confirmText: 'Delete',
        cancelText: 'Cancel',
        confirmColor: 'warn'
      }
    });

    ref.afterClosed().subscribe(confirmed => {
      if (confirmed !== true) {
        return;
      }

      this.saving = true;
      this.error = null;
      this.vendorsService.deleteVendor(this.vendor!.id).subscribe({
        next: () => {
          this.vendorFlagNotificationsService.removeNotificationsForVendor(this.vendor!.id);
          this.saving = false;
          this.snackBar.open('Vendor deleted.', '', { duration: 4000 });
          this.router.navigate(['/residents/vendors']);
        },
        error: () => {
          this.error = 'Unable to delete vendor.';
          this.saving = false;
        }
      });
    });
  }

  ngOnDestroy(): void {
    this.appStateSub?.unsubscribe();
  }
}
