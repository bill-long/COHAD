import { TestBed } from '@angular/core/testing';
import { BehaviorSubject, of, throwError } from 'rxjs';
import { take, toArray } from 'rxjs/operators';
import { SuppressedAddressesService } from './suppressed-addresses.service';
import { EmailSuppressionService } from './email-suppression.service';
import { ApplicationState, applicationState, initialStateValue } from 'src/app/state';
import { EmailSuppression } from '../models';

const suppression = (email: string): EmailSuppression => ({
  id: 'id-' + email,
  email,
  reason: 'HardBounce',
  consecutiveFailureCount: 1,
  firstSeenUtc: '2026-08-01T00:00:00Z',
  lastSeenUtc: '2026-08-01T00:00:00Z',
  causingJobId: null,
  suppressedUtc: '2026-08-01T00:00:00Z',
  suppressedBy: 'system:delivery-event',
  providerDiagnostic: null,
  clearedUtc: null,
  clearedBy: null,
  isActive: true,
});

function adminState(): ApplicationState {
  return { ...initialStateValue, apiUser: { roles: ['Administrator'] } as any };
}

describe('SuppressedAddressesService', () => {
  let serviceSpy: jasmine.SpyObj<EmailSuppressionService>;
  let appState$: BehaviorSubject<ApplicationState>;

  function build(): SuppressedAddressesService {
    TestBed.configureTestingModule({
      providers: [
        SuppressedAddressesService,
        { provide: EmailSuppressionService, useValue: serviceSpy },
        { provide: applicationState, useValue: appState$.asObservable() },
      ],
    });
    return TestBed.inject(SuppressedAddressesService);
  }

  beforeEach(() => {
    serviceSpy = jasmine.createSpyObj('EmailSuppressionService', ['getSuppressions', 'createSuppression', 'clearSuppression']);
  });

  it('issues no request and returns an empty set for a non-Administrator', () => {
    appState$ = new BehaviorSubject<ApplicationState>(initialStateValue);
    const service = build();

    let result: ReadonlySet<string> | undefined;
    service.activeSuppressed$.subscribe(s => (result = s));

    expect(result?.size).toBe(0);
    expect(serviceSpy.getSuppressions).not.toHaveBeenCalled();
  });

  it('normalizes the fetched addresses for an Administrator', () => {
    appState$ = new BehaviorSubject<ApplicationState>(adminState());
    serviceSpy.getSuppressions.and.returnValue(of([suppression(' Taylor.Old@Example.COM ')]));
    const service = build();

    let result: ReadonlySet<string> | undefined;
    service.activeSuppressed$.subscribe(s => (result = s));

    expect(result?.has('taylor.old@example.com')).toBeTrue();
  });

  it('degrades to an empty set when the fetch fails', () => {
    appState$ = new BehaviorSubject<ApplicationState>(adminState());
    serviceSpy.getSuppressions.and.returnValue(throwError(() => new Error('boom')));
    const service = build();

    let result: ReadonlySet<string> | undefined;
    service.activeSuppressed$.subscribe(s => (result = s));

    expect(result?.size).toBe(0);
  });

  it('isSuppressed matches a typed address against the latest set, normalizing both', () => {
    appState$ = new BehaviorSubject<ApplicationState>(adminState());
    serviceSpy.getSuppressions.and.returnValue(of([suppression('taylor.old@cohad.local')]));
    const service = build();

    expect(service.isSuppressed(' Taylor.Old@COHAD.local ')).toBeTrue();
    expect(service.isSuppressed('someone.else@example.com')).toBeFalse();
    expect(service.isSuppressed(null)).toBeFalse();
    expect(service.isSuppressed('')).toBeFalse();
  });

  it('isSuppressed is always false for a non-Administrator (no set fetched)', () => {
    appState$ = new BehaviorSubject<ApplicationState>(initialStateValue);
    const service = build();

    expect(service.isSuppressed('taylor.old@cohad.local')).toBeFalse();
    expect(serviceSpy.getSuppressions).not.toHaveBeenCalled();
  });

  it('re-fetches on refresh() so the set reflects a create/clear in the same session', () => {
    appState$ = new BehaviorSubject<ApplicationState>(adminState());
    serviceSpy.getSuppressions.and.returnValues(of([suppression('a@example.com')]), of([]));
    const service = build();

    const emissions: ReadonlySet<string>[] = [];
    service.activeSuppressed$.pipe(take(2), toArray()).subscribe(sets => emissions.push(...sets));
    service.refresh();

    expect(serviceSpy.getSuppressions).toHaveBeenCalledTimes(2);
    expect(emissions[0].has('a@example.com')).toBeTrue();
    expect(emissions[1].size).toBe(0);
  });
});
