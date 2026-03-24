import { Component, OnInit } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { FormBuilder } from '@angular/forms';
import { MatDialog } from '@angular/material/dialog';
import { VendorDetail, VendorReview, VendorsService, vendorCategoryClass } from 'src/app/services/vendors.service';
import { ConfirmDialogComponent } from '../confirm-dialog/confirm-dialog.component';

@Component({
  selector: 'app-vendor-detail',
  templateUrl: './vendor-detail.component.html',
  styleUrls: ['./vendor-detail.component.css'],
  standalone: false
})
export class VendorDetailComponent implements OnInit {
  loading = false;
  saving = false;
  error: string | null = null;
  vendor: VendorDetail | null = null;
  editingReviewId: string | null = null;

  readonly reviewForm = this.formBuilder.group({
    reviewText: ['']
  });

  readonly editReviewForm = this.formBuilder.group({
    reviewText: ['']
  });

  private static readonly minNewReviewLength = 10;

  constructor(
    private readonly route: ActivatedRoute,
    private readonly formBuilder: FormBuilder,
    private readonly vendorsService: VendorsService,
    private readonly dialog: MatDialog) { }

  ngOnInit(): void {
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
}
