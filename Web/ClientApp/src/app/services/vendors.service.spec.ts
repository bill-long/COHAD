import { TestBed } from '@angular/core/testing';
import { HttpClientTestingModule, HttpTestingController } from '@angular/common/http/testing';
import { getVendorCategoryBucket, VENDOR_CATEGORY_BUCKETS, VendorsService } from './vendors.service';

describe('VendorsService', () => {
  let service: VendorsService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [HttpClientTestingModule],
    });
    service = TestBed.inject(VendorsService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('builds vendor query with filters', () => {
    service.getVendors('plumb', 'Plumbing', true).subscribe();

    const req = httpMock.expectOne('api/vendors?q=plumb&category=Plumbing&neighborOnly=true');
    expect(req.request.method).toBe('GET');
    req.flush([]);
  });

  it('calls youth services endpoint without filters', () => {
    service.getYouthServices().subscribe();

    const req = httpMock.expectOne('api/youthservices');
    expect(req.request.method).toBe('GET');
    req.flush([]);
  });

  it('exposes five broad vendor category buckets', () => {
    expect(VENDOR_CATEGORY_BUCKETS).toEqual([
      'Home Construction & Repair',
      'Outdoor & Property',
      'Home Services & Household',
      'Personal, Health & Professional',
      'Auto, Pet & Community',
    ]);
  });

  it('maps detailed categories to broad buckets', () => {
    expect(getVendorCategoryBucket('Roofer')).toBe('Home Construction & Repair');
    expect(getVendorCategoryBucket('Landscape')).toBe('Outdoor & Property');
    expect(getVendorCategoryBucket('Housekeeper')).toBe('Home Services & Household');
    expect(getVendorCategoryBucket('Chiropractor')).toBe('Personal, Health & Professional');
    expect(getVendorCategoryBucket('Mechanic')).toBe('Auto, Pet & Community');
  });

  it('falls back unknown categories to Auto, Pet & Community bucket', () => {
    expect(getVendorCategoryBucket('Rare New Category')).toBe('Auto, Pet & Community');
  });
});
