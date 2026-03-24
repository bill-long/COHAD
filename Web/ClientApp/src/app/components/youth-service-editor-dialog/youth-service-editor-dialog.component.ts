import { Component, Inject } from '@angular/core';
import { FormBuilder } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import {
  YouthServiceListing,
  YouthServiceUpsertPayload,
  VendorsService
} from 'src/app/services/vendors.service';
import { normalizeOptionalUsPhoneForStorage } from 'src/app/utils/format-phone';

export interface YouthServiceEditorDialogData {
  /** Pre-fill services when opening from a filtered list (e.g. "Babysit") */
  presetService?: string | null;
}

@Component({
  selector: 'app-youth-service-editor-dialog',
  templateUrl: './youth-service-editor-dialog.component.html',
  styleUrls: ['./youth-service-editor-dialog.component.css'],
  standalone: false
})
export class YouthServiceEditorDialogComponent {
  saving = false;
  error: string | null = null;

  readonly form = this.formBuilder.group({
    name: [''],
    services: [''],
    bornYear: [''],
    phone: [''],
    contactMethod: ['Text'],
    email: [''],
    address: [''],
    parentNote: ['']
  });

  constructor(
    private readonly formBuilder: FormBuilder,
    private readonly vendorsService: VendorsService,
    public readonly dialogRef: MatDialogRef<
      YouthServiceEditorDialogComponent,
      YouthServiceListing | null
    >,
    @Inject(MAT_DIALOG_DATA) public readonly data: YouthServiceEditorDialogData
  ) {
    if (data?.presetService?.trim()) {
      this.form.patchValue({ services: data.presetService.trim() });
    }
  }

  cancel(): void {
    this.dialogRef.close(null);
  }

  save(): void {
    const raw = this.form.getRawValue();
    if (!(raw.name ?? '').trim()) {
      this.error = 'Name is required.';
      return;
    }

    const phoneNorm = normalizeOptionalUsPhoneForStorage(raw.phone);
    if (!phoneNorm.ok) {
      this.error = phoneNorm.message;
      return;
    }

    const parsedBornYear = Number(raw.bornYear);
    const payload: YouthServiceUpsertPayload = {
      name: (raw.name ?? '').trim(),
      services: (raw.services ?? '')
        .split(',')
        .map(v => v.trim())
        .filter(v => v.length > 0),
      bornYear: Number.isFinite(parsedBornYear) && parsedBornYear > 0 ? parsedBornYear : null,
      phone: phoneNorm.value,
      contactMethod: raw.contactMethod === 'Call' ? 'Call' : 'Text',
      email: (raw.email ?? '').trim(),
      address: (raw.address ?? '').trim(),
      parentNote: (raw.parentNote ?? '').trim()
    };

    this.saving = true;
    this.error = null;
    this.vendorsService.createYouthService(payload).subscribe({
      next: (created) => {
        this.saving = false;
        this.dialogRef.close(created);
      },
      error: () => {
        this.error = 'Unable to create listing.';
        this.saving = false;
      }
    });
  }
}
