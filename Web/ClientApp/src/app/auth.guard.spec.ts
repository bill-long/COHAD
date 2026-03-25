import { TestBed, fakeAsync, tick } from '@angular/core/testing';
import { BehaviorSubject } from 'rxjs';
import { Router } from '@angular/router';
import { AuthGuard, AUTH_RESOLVE_TIMEOUT_MS } from './auth.guard';
import { ApplicationState, applicationState, initialStateValue } from './state';
import { AuthService } from './services/auth.service';

describe('AuthGuard', () => {
  let appState$: BehaviorSubject<ApplicationState>;
  let guard: AuthGuard;
  let router: jasmine.SpyObj<Router>;

  beforeEach(() => {
    appState$ = new BehaviorSubject<ApplicationState>(initialStateValue);
    router = jasmine.createSpyObj<Router>('Router', ['navigate']);
    router.navigate.and.resolveTo(true);

    TestBed.configureTestingModule({
      providers: [
        AuthGuard,
        { provide: applicationState, useValue: appState$ },
        { provide: Router, useValue: router },
        { provide: AuthService, useValue: {} }
      ]
    });

    guard = TestBed.inject(AuthGuard);
  });

  it('allows activation when auth session is resolved and authenticated', () => {
    appState$.next({
      ...initialStateValue,
      authSessionResolved: true,
      authUser: {
        accessToken: 'token',
        identityClaims: {
          emails: ['user@example.com'],
          family_name: 'User',
          given_name: 'Test',
          idp: 'idp',
          sub: 'sub',
          streetAddress: '123 Mock Lane'
        }
      }
    });

    let result: boolean | undefined;
    guard.canActivate({} as any, {} as any).subscribe(v => (result = v));

    expect(result).toBeTrue();
    expect(router.navigate).not.toHaveBeenCalled();
  });

  it('denies activation when auth session resolves without an authenticated user', () => {
    appState$.next({
      ...initialStateValue,
      authSessionResolved: true,
      authUser: null
    });

    let result: boolean | undefined;
    guard.canActivate({} as any, {} as any).subscribe(v => (result = v));

    expect(result).toBeFalse();
    expect(router.navigate).toHaveBeenCalledOnceWith(['/']);
  });

  it('denies activation after timeout if auth session never resolves', fakeAsync(() => {
    let result: boolean | undefined;
    guard.canActivate({} as any, {} as any).subscribe(v => (result = v));

    tick(AUTH_RESOLVE_TIMEOUT_MS - 1);
    expect(result).toBeUndefined();
    expect(router.navigate).not.toHaveBeenCalled();

    tick(1);
    expect(result).toBeFalse();
    expect(router.navigate).toHaveBeenCalledOnceWith(['/']);
  }));
});
