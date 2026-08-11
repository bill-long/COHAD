import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { Subject, of, throwError } from 'rxjs';
import { ManageSuppressionsComponent } from './manage-suppressions.component';
import { EmailSuppressionService } from 'src/app/services/email-suppression.service';
import { SuppressedAddressesService } from 'src/app/services/suppressed-addresses.service';
import { EmailSuppression } from 'src/app/models';

const makeSuppression = (overrides: Partial<EmailSuppression> = {}): EmailSuppression => ({
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
  ...overrides,
});

describe('ManageSuppressionsComponent', () => {
  let component: ManageSuppressionsComponent;
  let serviceSpy: jasmine.SpyObj<EmailSuppressionService>;
  let suppressedAddressesSpy: jasmine.SpyObj<SuppressedAddressesService>;

  beforeEach(() => {
    serviceSpy = jasmine.createSpyObj('EmailSuppressionService', ['getSuppressions', 'createSuppression', 'clearSuppression']);
    serviceSpy.getSuppressions.and.returnValue(of([makeSuppression()]));
    suppressedAddressesSpy = jasmine.createSpyObj('SuppressedAddressesService', ['refresh']);

    TestBed.configureTestingModule({
      providers: [
        ManageSuppressionsComponent,
        { provide: EmailSuppressionService, useValue: serviceSpy },
        { provide: SuppressedAddressesService, useValue: suppressedAddressesSpy },
      ],
    });

    component = TestBed.inject(ManageSuppressionsComponent);
  });

  it('loads active suppressions on init', () => {
    component.ngOnInit();

    expect(serviceSpy.getSuppressions).toHaveBeenCalledWith(false);
    expect(component.suppressions.length).toBe(1);
    expect(component.loading).toBeFalse();
  });

  it('reloads with includeCleared when the toggle changes', () => {
    component.ngOnInit();
    serviceSpy.getSuppressions.calls.reset();
    serviceSpy.getSuppressions.and.returnValue(of([makeSuppression(), makeSuppression({ id: 'old', isActive: false })]));

    component.onIncludeClearedChange(true);

    expect(serviceSpy.getSuppressions).toHaveBeenCalledWith(true);
    expect(component.suppressions.length).toBe(2);
  });

  it('applies only the newest load response when two overlap (out-of-order guard)', () => {
    // Two loads in flight (a fast checkbox toggle): the later request wins even if its response
    // arrives first, so the table never contradicts the checkbox.
    const firstResponse = new Subject<EmailSuppression[]>();
    const secondResponse = new Subject<EmailSuppression[]>();
    serviceSpy.getSuppressions.and.returnValues(firstResponse.asObservable(), secondResponse.asObservable());

    component.onIncludeClearedChange(true); // load #1 (includeCleared=true)
    component.onIncludeClearedChange(false); // load #2 (includeCleared=false) - the latest intent

    // The newer request resolves first...
    secondResponse.next([makeSuppression({ id: 'active' })]);
    // ...then the stale first request resolves and must be ignored.
    firstResponse.next([makeSuppression({ id: 'active' }), makeSuppression({ id: 'cleared', isActive: false })]);

    expect(component.suppressions.length).toBe(1);
    expect(component.suppressions[0].id).toBe('active');
  });

  it('sets errorText when the list fails to load', () => {
    serviceSpy.getSuppressions.and.returnValue(throwError(() => new HttpErrorResponse({ status: 500 })));
    component.ngOnInit();

    expect(component.errorText).toBeTruthy();
    expect(component.loading).toBeFalse();
  });

  it('requires an address containing @ before creating', () => {
    component.newEmail = 'not-an-address';
    expect(component.canCreate).toBeFalse();

    component.create();
    expect(serviceSpy.createSuppression).not.toHaveBeenCalled();
  });

  it('creates a suppression, clears the input, and reloads', () => {
    component.ngOnInit();
    serviceSpy.getSuppressions.calls.reset();
    serviceSpy.getSuppressions.and.returnValue(of([makeSuppression(), makeSuppression({ id: 'new' })]));
    serviceSpy.createSuppression.and.returnValue(of(makeSuppression({ id: 'new' })));
    component.newEmail = '  someone@example.com  ';

    component.create();

    expect(serviceSpy.createSuppression).toHaveBeenCalledWith('someone@example.com');
    expect(component.newEmail).toBe('');
    expect(serviceSpy.getSuppressions).toHaveBeenCalledTimes(1);
    // The address-editor chip cache is refreshed so it does not disagree with the list.
    expect(suppressedAddressesSpy.refresh).toHaveBeenCalledTimes(1);
  });

  it('clears a suppression and reloads', () => {
    component.ngOnInit();
    serviceSpy.getSuppressions.calls.reset();
    serviceSpy.getSuppressions.and.returnValue(of([]));
    serviceSpy.clearSuppression.and.returnValue(
      of({ suppression: makeSuppression({ isActive: false, clearedUtc: '2026-08-09T00:00:00Z' }), providerWarning: null }),
    );

    component.clear(makeSuppression());

    expect(serviceSpy.clearSuppression).toHaveBeenCalledWith('abc');
    expect(serviceSpy.getSuppressions).toHaveBeenCalledTimes(1);
    expect(suppressedAddressesSpy.refresh).toHaveBeenCalledTimes(1);
    expect(component.warningText).toBeNull();
  });

  it('keeps the provider warning visible through the reload a clear triggers', () => {
    // A ProviderUnsubscribe clear whose provider-side reactivation failed: the local clear
    // succeeded (rows reload), but the warning must survive that reload or the admin never
    // learns the address is still suppressed at the provider.
    component.ngOnInit();
    serviceSpy.getSuppressions.and.returnValue(of([]));
    serviceSpy.clearSuppression.and.returnValue(
      of({
        suppression: makeSuppression({ isActive: false, clearedUtc: '2026-08-09T00:00:00Z' }),
        providerWarning: 'still suppressed at the provider',
      }),
    );

    component.clear(makeSuppression());

    expect(component.warningText).toBe('still suppressed at the provider');
    expect(component.errorText).toBeNull();
  });

  it('clears the provider warning on the next user-initiated load', () => {
    component.ngOnInit();
    serviceSpy.getSuppressions.and.returnValue(of([]));
    serviceSpy.clearSuppression.and.returnValue(
      of({
        suppression: makeSuppression({ isActive: false, clearedUtc: '2026-08-09T00:00:00Z' }),
        providerWarning: 'still suppressed at the provider',
      }),
    );
    component.clear(makeSuppression());
    expect(component.warningText).toBeTruthy();

    component.onIncludeClearedChange(true);

    expect(component.warningText).toBeNull();
  });

  it('surfaces a 409 as try-again guidance, not an error state', () => {
    serviceSpy.clearSuppression.and.returnValue(throwError(() => new HttpErrorResponse({ status: 409 })));

    component.clear(makeSuppression());

    expect(component.noticeText).toContain('try again');
    expect(component.errorText).toBeNull();
  });

  it('clears a stale 409 notice on the next load (toggle/reload)', () => {
    serviceSpy.clearSuppression.and.returnValue(throwError(() => new HttpErrorResponse({ status: 409 })));
    component.clear(makeSuppression());
    expect(component.noticeText).toContain('try again');

    // A subsequent successful toggle/reload must not leave the stale notice above fresh rows.
    serviceSpy.getSuppressions.and.returnValue(of([makeSuppression()]));
    component.onIncludeClearedChange(true);

    expect(component.noticeText).toBeNull();
  });

  it('surfaces a non-409 write failure as an error', () => {
    serviceSpy.createSuppression.and.returnValue(throwError(() => new HttpErrorResponse({ status: 500 })));
    component.newEmail = 'someone@example.com';

    component.create();

    expect(component.errorText).toBeTruthy();
    expect(component.noticeText).toBeNull();
  });
});
