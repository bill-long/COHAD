import { Component, OnInit } from '@angular/core';
import { FormBuilder } from '@angular/forms';
import { Router } from '@angular/router';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { VendorSummary, VendorsService, vendorCategoryClass } from 'src/app/services/vendors.service';
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
  categories: string[] = [];

  readonly filterForm = this.formBuilder.group({
    search: [''],
    category: [''],
    neighborOnly: [false]
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
        const categorySet = new Set<string>();
        vendors.forEach(vendor => (vendor.categories ?? []).forEach(category => categorySet.add(category)));
        this.categories = Array.from(categorySet).sort((a, b) => a.localeCompare(b));
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

    this.vendors = this.allVendors.filter(vendor => {
      if (neighborOnly && !vendor.isNeighborAffiliated) {
        return false;
      }

      if (category && !(vendor.categories ?? []).some(c => c.toLowerCase() === category.toLowerCase())) {
        return false;
      }

      if (!search) {
        return true;
      }

      const haystack = [
        vendor.name ?? '',
        ...(vendor.categories ?? []),
        vendor.email ?? '',
        vendor.phone ?? ''
      ].join(' ').toLowerCase();
      return haystack.includes(search);
    });
  }

  categoryClass = vendorCategoryClass;

  openAddVendor(): void {
    const presetCategory = (this.filterForm.controls.category.value ?? '').trim();
    const ref = this.dialog.open(VendorEditorDialogComponent, {
      data: { presetCategory: presetCategory || null } as VendorEditorDialogData,
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
