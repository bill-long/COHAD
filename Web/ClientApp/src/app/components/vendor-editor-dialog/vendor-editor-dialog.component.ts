import { Component } from '@angular/core';
import { FormBuilder } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { Inject } from '@angular/core';
import { VendorDetail, VendorUpsertPayload, VendorsService } from 'src/app/services/vendors.service';
import { normalizeOptionalUsPhoneForStorage } from 'src/app/utils/format-phone';

export interface VendorEditorDialogData {
  presetCategory?: string | null;
  vendor?: VendorDetail | null;
}

@Component({
  selector: 'app-vendor-editor-dialog',
  templateUrl: './vendor-editor-dialog.component.html',
  styleUrls: ['./vendor-editor-dialog.component.css'],
  standalone: false,
})
export class VendorEditorDialogComponent {
  saving = false;
  error: string | null = null;

  readonly form = this.formBuilder.group({
    name: [''],
    categories: [''],
    isNeighborAffiliated: [false],
    phone: [''],
    email: [''],
    website: [''],
    address: [''],
    notes: [''],
    initialReviewText: [''],
  });

  constructor(
    private readonly formBuilder: FormBuilder,
    private readonly vendorsService: VendorsService,
    public readonly dialogRef: MatDialogRef<VendorEditorDialogComponent, VendorDetail | null>,
    @Inject(MAT_DIALOG_DATA) public readonly data: VendorEditorDialogData,
  ) {
    if (data?.vendor) {
      this.form.patchValue({
        name: data.vendor.name ?? '',
        categories: (data.vendor.categories ?? []).join(', '),
        isNeighborAffiliated: data.vendor.isNeighborAffiliated ?? false,
        phone: data.vendor.phone ?? '',
        email: data.vendor.email ?? '',
        website: data.vendor.website ?? '',
        address: data.vendor.address ?? '',
        notes: data.vendor.notes ?? '',
        initialReviewText: '',
      });
    } else if (data?.presetCategory) {
      this.form.patchValue({ categories: data.presetCategory });
    }
  }

  get isEditMode(): boolean {
    return !!this.data?.vendor;
  }

  cancel(): void {
    this.dialogRef.close(null);
  }

  save(): void {
    const raw = this.form.getRawValue();

    const name = (raw.name ?? '').trim();
    const initialReviewText = (raw.initialReviewText ?? '').trim();
    if (!name) {
      this.error = 'Vendor name is required.';
      return;
    }
    if (!this.isEditMode && !initialReviewText) {
      this.error = 'Initial review is required.';
      return;
    }

    const phoneNorm = normalizeOptionalUsPhoneForStorage(raw.phone);
    if (!phoneNorm.ok) {
      this.error = phoneNorm.message;
      return;
    }

    const payload: VendorUpsertPayload = {
      name,
      categories: (raw.categories ?? '')
        .split(',')
        .map(v => v.trim())
        .filter(v => v.length > 0),
      isNeighborAffiliated: raw.isNeighborAffiliated ?? false,
      phone: phoneNorm.value,
      email: (raw.email ?? '').trim(),
      website: (raw.website ?? '').trim(),
      address: (raw.address ?? '').trim(),
      notes: (raw.notes ?? '').trim(),
    };
    if (!this.isEditMode) {
      payload.initialReviewText = initialReviewText;
    }

    this.saving = true;
    this.error = null;
    const save$ = this.isEditMode
      ? this.vendorsService.updateVendor(this.data.vendor!.id, payload)
      : this.vendorsService.createVendor(payload);
    save$.subscribe({
      next: created => {
        this.saving = false;
        this.dialogRef.close(created);
      },
      error: () => {
        this.error = this.isEditMode ? 'Unable to update vendor.' : 'Unable to create vendor.';
        this.saving = false;
      },
    });
  }
}
