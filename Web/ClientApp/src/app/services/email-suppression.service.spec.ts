import { TestBed } from '@angular/core/testing';
import { HttpClientTestingModule, HttpTestingController } from '@angular/common/http/testing';
import { EmailSuppressionService } from './email-suppression.service';
import { EmailSuppression } from '../models';

describe('EmailSuppressionService', () => {
  let service: EmailSuppressionService;
  let httpMock: HttpTestingController;

  const suppression: EmailSuppression = {
    id: 'abc',
    email: 'taylor.old@cohad.local',
    reason: 'HardBounce',
    consecutiveFailureCount: 1,
    firstSeenUtc: '2026-08-01T00:00:00Z',
    lastSeenUtc: '2026-08-01T00:00:00Z',
    causingJobId: null,
    suppressedUtc: '2026-08-01T00:00:00Z',
    suppressedBy: 'system:delivery-event',
    providerDiagnostic: 'HardBounce: user unknown',
    clearedUtc: null,
    clearedBy: null,
    isActive: true,
  };

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [HttpClientTestingModule],
    });
    service = TestBed.inject(EmailSuppressionService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('lists active suppressions without the includeCleared parameter', () => {
    service.getSuppressions().subscribe();

    const req = httpMock.expectOne('api/email-suppressions');
    expect(req.request.method).toBe('GET');
    expect(req.request.params.get('includeCleared')).toBeNull();
    req.flush([suppression]);
  });

  it('passes includeCleared=true when requested', () => {
    service.getSuppressions(true).subscribe();

    const req = httpMock.expectOne('api/email-suppressions?includeCleared=true');
    expect(req.request.method).toBe('GET');
    req.flush([]);
  });

  it('creates a suppression with the address in the body', () => {
    service.createSuppression('someone@example.com').subscribe();

    const req = httpMock.expectOne('api/email-suppressions');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ email: 'someone@example.com' });
    req.flush(suppression);
  });

  it('clears a suppression by document id and surfaces the provider warning', () => {
    let providerWarning: string | null | undefined;
    service.clearSuppression('abc').subscribe(result => (providerWarning = result.providerWarning));

    const req = httpMock.expectOne('api/email-suppressions/abc/clear');
    expect(req.request.method).toBe('POST');
    req.flush({
      suppression: { ...suppression, clearedUtc: '2026-08-09T00:00:00Z', isActive: false },
      providerWarning: 'still suppressed at the provider',
    });

    expect(providerWarning).toBe('still suppressed at the provider');
  });
});
