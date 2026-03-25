import { Component, OnInit } from '@angular/core';
import { FormBuilder } from '@angular/forms';
import { Router } from '@angular/router';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { VENDOR_CATEGORY_BUCKETS, VendorSummary, VendorsService, getVendorCategoryBucket, vendorCategoryClass } from 'src/app/services/vendors.service';
import { VendorEditorDialogComponent, VendorEditorDialogData } from '../vendor-editor-dialog/vendor-editor-dialog.component';

@Component({
  selector: 'app-vendors',
  templateUrl: './vendors.component.html',
  styleUrls: ['./vendors.component.css'],
  standalone: false
})
export class VendorsComponent implements OnInit {
  loading = false;
  error: string | null = null;
  vendors: VendorSummary[] = [];
  private allVendors: VendorSummary[] = [];
  readonly categories = [...VENDOR_CATEGORY_BUCKETS];

  readonly filterForm = this.formBuilder.group({
    search: [''],
    category: [''],
    neighborOnly: [false],
    sortBy: ['recent']
  });

  constructor(
    private readonly vendorsService: VendorsService,
    private readonly formBuilder: FormBuilder,
    private readonly dialog: MatDialog,
    private readonly snackBar: MatSnackBar,
    private readonly router: Router
  ) { }

  ngOnInit(): void {
    this.filterForm.valueChanges.subscribe(() => this.applyFilters());
    this.load();
  }

  load(): void {
    this.loading = true;
    this.error = null;
    this.vendorsService.getVendors().subscribe({
      next: (vendors) => {
        this.allVendors = vendors;
        this.applyFilters();
        this.loading = false;
      },
      error: () => {
        this.error = 'Unable to load vendors.';
        this.loading = false;
      }
    });
  }

  private applyFilters(): void {
    const value = this.filterForm.getRawValue();
    const search = (value.search ?? '').trim().toLowerCase();
    const category = (value.category ?? '').trim();
    const neighborOnly = value.neighborOnly ?? false;
    const sortBy = (value.sortBy ?? 'recent').toLowerCase();

    const filtered = this.allVendors.filter(vendor => {
      if (neighborOnly && !vendor.isNeighborAffiliated) {
        return false;
      }

      if (category && !(vendor.categories ?? []).some(c => getVendorCategoryBucket(c) === category)) {
        return false;
      }

      if (!search) {
        return true;
      }

      const haystack = [
        vendor.name ?? '',
        ...(vendor.categories ?? []),
        vendor.email ?? '',
        vendor.phone ?? '',
        vendor.notes ?? ''
      ].join(' ').toLowerCase();
      return haystack.includes(search);
    });

    this.vendors = filtered.sort((a, b) => {
      if (sortBy === 'name') {
        return (a.name ?? '').localeCompare(b.name ?? '');
      }

      const aParsed = a.lastReviewModifiedUtc ? Date.parse(a.lastReviewModifiedUtc) : Number.NEGATIVE_INFINITY;
      const bParsed = b.lastReviewModifiedUtc ? Date.parse(b.lastReviewModifiedUtc) : Number.NEGATIVE_INFINITY;
      const aTs = Number.isFinite(aParsed) ? aParsed : Number.NEGATIVE_INFINITY;
      const bTs = Number.isFinite(bParsed) ? bParsed : Number.NEGATIVE_INFINITY;
      if (aTs === bTs) {
        return (a.name ?? '').localeCompare(b.name ?? '');
      }
      return bTs - aTs;
    });
  }

  categoryClass = vendorCategoryClass;

  openAddVendor(): void {
    const ref = this.dialog.open(VendorEditorDialogComponent, {
      data: { presetCategory: null } as VendorEditorDialogData,
      width: '720px',
      maxWidth: 'calc(100vw - 32px)',
      maxHeight: '90vh'
    });

    ref.afterClosed().subscribe(created => {
      if (!created) {
        return;
      }

      this.load();
      this.router.navigate(['/residents/vendors', created.id]);
      this.snackBar.open('Vendor created.', '', { duration: 4000 });
    });
  }

  openVendor(vendorId: string): void {
    this.router.navigate(['/residents/vendors', vendorId]);
  }
}
