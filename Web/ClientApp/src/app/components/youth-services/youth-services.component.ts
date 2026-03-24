import { Component, OnInit } from '@angular/core';
import { FormBuilder } from '@angular/forms';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { YouthServiceListing, VendorsService } from 'src/app/services/vendors.service';
import {
  YouthServiceEditorDialogComponent,
  YouthServiceEditorDialogData
} from '../youth-service-editor-dialog/youth-service-editor-dialog.component';

@Component({
  selector: 'app-youth-services',
  templateUrl: './youth-services.component.html',
  styleUrls: ['./youth-services.component.css'],
  standalone: false
})
export class YouthServicesComponent implements OnInit {
  loading = false;
  error: string | null = null;
  listings: YouthServiceListing[] = [];
  knownServices: string[] = [];

  readonly filterForm = this.formBuilder.group({
    search: [''],
    service: ['']
  });

  constructor(
    private readonly vendorsService: VendorsService,
    private readonly formBuilder: FormBuilder,
    private readonly dialog: MatDialog,
    private readonly snackBar: MatSnackBar
  ) { }

  private readonly serviceChipClasses = [
    'chip-blue',
    'chip-green',
    'chip-amber',
    'chip-purple',
    'chip-rose',
    'chip-cyan',
    'chip-teal',
    'chip-slate'
  ];

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading = true;
    this.error = null;
    const filter = this.filterForm.getRawValue();
    this.vendorsService.getYouthServices(filter.search ?? '', filter.service ?? '').subscribe({
      next: (items) => {
        this.listings = items;
        const serviceSet = new Set<string>();
        items.forEach(item => (item.services ?? []).forEach(service => serviceSet.add(service)));
        this.knownServices = Array.from(serviceSet).sort((a, b) => a.localeCompare(b));
        this.loading = false;
      },
      error: () => {
        this.error = 'Unable to load youth services.';
        this.loading = false;
      }
    });
  }

  openAddListing(): void {
    const presetService = (this.filterForm.controls.service.value ?? '').trim();
    const ref = this.dialog.open(YouthServiceEditorDialogComponent, {
      data: { presetService: presetService || null } as YouthServiceEditorDialogData,
      width: '720px',
      maxWidth: 'calc(100vw - 32px)',
      maxHeight: '90vh'
    });

    ref.afterClosed().subscribe(created => {
      if (!created) {
        return;
      }

      this.snackBar.open('Listing created.', undefined, { duration: 4000 });
      this.load();
      window.scrollTo({ top: 0, behavior: 'smooth' });
    });
  }

  openEditListing(listing: YouthServiceListing): void {
    if (listing.canEdit === false) {
      return;
    }

    const ref = this.dialog.open(YouthServiceEditorDialogComponent, {
      data: { listing } as YouthServiceEditorDialogData,
      width: '720px',
      maxWidth: 'calc(100vw - 32px)',
      maxHeight: '90vh'
    });

    ref.afterClosed().subscribe(updated => {
      if (!updated) {
        return;
      }

      this.snackBar.open('Listing updated.', undefined, { duration: 4000 });
      this.load();
      window.scrollTo({ top: 0, behavior: 'smooth' });
    });
  }

  serviceClass(service: string): string {
    const normalized = (service ?? '').toLowerCase().trim();
    let hash = 0;
    for (let i = 0; i < normalized.length; i++) {
      hash = ((hash << 5) - hash) + normalized.charCodeAt(i);
      hash |= 0;
    }
    const idx = Math.abs(hash) % this.serviceChipClasses.length;
    return this.serviceChipClasses[idx];
  }
}
