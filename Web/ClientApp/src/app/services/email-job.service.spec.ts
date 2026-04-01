import { TestBed } from '@angular/core/testing';
import { HttpClientTestingModule, HttpTestingController } from '@angular/common/http/testing';
import { EmailJobService } from './email-job.service';

describe('EmailJobService', () => {
  let service: EmailJobService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [HttpClientTestingModule]
    });
    service = TestBed.inject(EmailJobService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('fetches recent jobs with default limit', () => {
    service.getRecentJobs().subscribe();

    const req = httpMock.expectOne('api/email/jobs?limit=50');
    expect(req.request.method).toBe('GET');
    req.flush([]);
  });

  it('fetches recent jobs with custom limit', () => {
    service.getRecentJobs(10).subscribe();

    const req = httpMock.expectOne('api/email/jobs?limit=10');
    expect(req.request.method).toBe('GET');
    req.flush([]);
  });

  it('fetches a single job by id', () => {
    service.getJob('abc-123').subscribe();

    const req = httpMock.expectOne('api/email/jobs/abc-123');
    expect(req.request.method).toBe('GET');
    req.flush({});
  });

  it('retries a job via POST', () => {
    service.retryJob('abc-123').subscribe();

    const req = httpMock.expectOne('api/email/jobs/abc-123/retry');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toBeNull();
    req.flush({});
  });

  it('cancels a job via POST', () => {
    service.cancelJob('abc-123').subscribe();

    const req = httpMock.expectOne('api/email/jobs/abc-123/cancel');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toBeNull();
    req.flush({});
  });
});
