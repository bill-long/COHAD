import { TestBed } from '@angular/core/testing';
import { HttpClientTestingModule, HttpTestingController } from '@angular/common/http/testing';
import { VendorsService } from './vendors.service';

describe('VendorsService', () => {
  let service: VendorsService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [HttpClientTestingModule]
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
});
