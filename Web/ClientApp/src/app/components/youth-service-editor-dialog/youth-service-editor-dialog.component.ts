import { Component, Inject } from '@angular/core';
import { FormBuilder } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { MatAutocompleteSelectedEvent } from '@angular/material/autocomplete';
import { applicationState, ApplicationState } from 'src/app/state';
import {
  YouthServiceListing,
  YouthServiceUpsertPayload,
  VendorsService
} from 'src/app/services/vendors.service';
import { ApiUser } from 'src/app/models';
import { normalizeOptionalUsPhoneForStorage } from 'src/app/utils/format-phone';
import { Observable, of } from 'rxjs';
import { switchMap, take } from 'rxjs/operators';

export interface YouthServiceEditorDialogData {
  /** Pre-fill services when opening from a filtered list (e.g. "Babysit") */
  presetService?: string | null;
  listing?: YouthServiceListing | null;
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
  isAdmin = false;
  owners: ApiUser[] = [];
  filteredOwners: ApiUser[] = [];

  readonly form = this.formBuilder.group({
    name: [''],
    services: [''],
    bornYear: [''],
    phone: [''],
    contactMethod: ['Text'],
    email: [''],
    address: [''],
    parentNote: [''],
    ownerSearch: [''],
    ownerUniqueId: ['']
  });

  constructor(
    private readonly formBuilder: FormBuilder,
    private readonly vendorsService: VendorsService,
    @Inject(applicationState) private readonly appState: Observable<ApplicationState>,
    public readonly dialogRef: MatDialogRef<
      YouthServiceEditorDialogComponent,
      YouthServiceListing | null
    >,
    @Inject(MAT_DIALOG_DATA) public readonly data: YouthServiceEditorDialogData
  ) {
    if (data?.listing) {
      this.form.patchValue({
        name: data.listing.name ?? '',
        services: (data.listing.services ?? []).join(', '),
        bornYear: data.listing.bornYear ? String(data.listing.bornYear) : '',
        phone: data.listing.phone ?? '',
        contactMethod: data.listing.contactMethod ?? 'Text',
        email: data.listing.email ?? '',
        address: data.listing.address ?? '',
        parentNote: data.listing.parentNote ?? '',
        ownerSearch: '',
        ownerUniqueId: data.listing.ownerUniqueId ?? ''
      });
    } else if (data?.presetService?.trim()) {
      this.form.patchValue({ services: data.presetService.trim() });
    }

    this.appState.pipe(take(1)).subscribe(state => {
      this.isAdmin = state.apiUser?.roles?.includes('Administrator') ?? false;
      if (this.isAdmin && this.isEditMode) {
        this.vendorsService.getUsers().subscribe({
          next: users => {
            this.owners = [...users].sort((a, b) => a.displayName.localeCompare(b.displayName));
            this.filteredOwners = this.owners.slice(0, 8);
            if (!(this.form.controls.ownerUniqueId.value ?? '').trim()) {
              this.form.patchValue({ ownerUniqueId: this.data.listing?.ownerUniqueId ?? '' });
            }
            this.syncOwnerSearchFromOwnerId();
          },
          error: () => {
            this.owners = [];
            this.filteredOwners = [];
          }
        });
      }
    });

    this.form.controls.ownerSearch.valueChanges.subscribe(raw => {
      const query = (raw ?? '').toLowerCase().trim();
      const currentOwnerId = (this.form.controls.ownerUniqueId.value ?? '').trim();
      const currentOwner = this.owners.find(owner => owner.uniqueId === currentOwnerId);
      const currentOwnerLabel = currentOwner ? this.ownerLabel(currentOwner) : '';
      if ((raw ?? '') !== currentOwnerLabel) {
        this.form.patchValue({ ownerUniqueId: '' }, { emitEvent: false });
      }

      if (!query) {
        this.filteredOwners = this.owners.slice(0, 8);
        return;
      }

      this.filteredOwners = this.owners
        .filter(owner =>
          owner.displayName.toLowerCase().includes(query) ||
          owner.email.toLowerCase().includes(query))
        .slice(0, 8);
    });
  }

  get isEditMode(): boolean {
    return !!this.data?.listing;
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
    const requestedOwnerUniqueId = (raw.ownerUniqueId ?? '').trim();
    const currentOwnerUniqueId = (this.data?.listing?.ownerUniqueId ?? '').trim();
    const shouldReassignOwner = this.isEditMode &&
      this.isAdmin &&
      requestedOwnerUniqueId.length > 0 &&
      requestedOwnerUniqueId !== currentOwnerUniqueId;

    this.saving = true;
    this.error = null;
    const save$ = this.isEditMode
      ? this.vendorsService.updateYouthService(this.data.listing!.id, payload)
      : this.vendorsService.createYouthService(payload);
    save$.pipe(
      switchMap(saved => {
        if (!shouldReassignOwner) {
          return of(saved);
        }
        return this.vendorsService.reassignYouthServiceOwner(saved.id, requestedOwnerUniqueId);
      })
    ).subscribe({
      next: (created) => {
        this.saving = false;
        this.dialogRef.close(created);
      },
      error: () => {
        this.error = this.isEditMode ? 'Unable to update listing.' : 'Unable to create listing.';
        this.saving = false;
      }
    });
  }

  onOwnerSelected(event: MatAutocompleteSelectedEvent): void {
    const selectedOwnerId = (event.option.value ?? '').toString();
    const selectedOwner = this.owners.find(owner => owner.uniqueId === selectedOwnerId);
    if (!selectedOwner) {
      return;
    }

    this.form.patchValue({
      ownerUniqueId: selectedOwner.uniqueId,
      ownerSearch: this.ownerLabel(selectedOwner)
    }, { emitEvent: false });
  }

  ownerLabel(owner: ApiUser): string {
    return `${owner.displayName} (${owner.email})`;
  }

  private syncOwnerSearchFromOwnerId(): void {
    const ownerId = (this.form.controls.ownerUniqueId.value ?? '').trim();
    if (!ownerId) {
      return;
    }

    const owner = this.owners.find(u => u.uniqueId === ownerId);
    if (!owner) {
      return;
    }

    this.form.patchValue({
      ownerSearch: this.ownerLabel(owner)
    }, { emitEvent: false });
  }
}
