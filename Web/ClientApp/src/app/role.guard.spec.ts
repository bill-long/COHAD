import { TestBed, fakeAsync, tick } from '@angular/core/testing';
import { BehaviorSubject, Subject } from 'rxjs';
import { Router } from '@angular/router';
import { RoleGuard, ROLE_RESOLVE_TIMEOUT_MS } from './role.guard';
import { ApplicationState, applicationState, initialStateValue, dispatcher, Action, Login } from './state';
import { ApiUser, AuthUser } from './models';

function makeApiUser(roles: string[]): ApiUser {
  return {
    uniqueId: 'user-1',
    createdTime: '',
    modifiedTime: '',
    creatorId: '',
    modifierId: '',
    givenName: 'Test',
    surname: 'User',
    displayName: 'Test User',
    identityProvider: 'idp',
    email: 'test@example.com',
    streetAddress: '',
    roles,
    ownedHomes: [],
  };
}

function stubAuthUser(): AuthUser {
  return {
    accessToken: 'token',
    identityClaims: {
      emails: ['test@example.com'],
      family_name: 'User',
      given_name: 'Test',
      idp: 'idp',
      sub: 'sub',
      streetAddress: '',
    },
  };
}

// A resolved, bootstrap-complete state. When an apiUser is present the principal is authenticated,
// so default authUser to a stub; pass it explicitly to model an authenticated-but-unprovisioned user.
function completedState(apiUser: ApiUser | null, authUser: AuthUser | null = apiUser ? stubAuthUser() : null): ApplicationState {
  return {
    ...initialStateValue,
    authSessionResolved: true,
    authBootstrapStatus: 'completed',
    authUser,
    apiUser,
  };
}

describe('RoleGuard', () => {
  let appState$: BehaviorSubject<ApplicationState>;
  let dispatcher$: Subject<Action>;
  let dispatched: Action[];
  let guard: RoleGuard;
  let router: jasmine.SpyObj<Router>;

  beforeEach(() => {
    appState$ = new BehaviorSubject<ApplicationState>(initialStateValue);
    dispatcher$ = new Subject<Action>();
    dispatched = [];
    dispatcher$.subscribe(a => dispatched.push(a));
    router = jasmine.createSpyObj<Router>('Router', ['navigate']);
    router.navigate.and.resolveTo(true);

    TestBed.configureTestingModule({
      providers: [
        RoleGuard,
        { provide: applicationState, useValue: appState$ },
        { provide: dispatcher, useValue: dispatcher$ },
        { provide: Router, useValue: router },
      ],
    });

    guard = TestBed.inject(RoleGuard);
  });

  it('allows activation when user has a required role', () => {
    appState$.next(completedState(makeApiUser(['Resident'])));

    let result: boolean | undefined;
    guard.canActivate({ data: { allowedRoles: ['Resident', 'Administrator'] } } as any, { url: '/manage' } as any).subscribe(v => (result = v));

    expect(result).toBeTrue();
    expect(router.navigate).not.toHaveBeenCalled();
    expect(dispatched.length).toBe(0);
  });

  it('bounces an authenticated user who lacks the required role to home without triggering login', () => {
    appState$.next(completedState(makeApiUser(['WelcomeCommittee'])));

    let result: boolean | undefined;
    guard.canActivate({ data: { allowedRoles: ['Resident', 'Administrator'] } } as any, { url: '/manage' } as any).subscribe(v => (result = v));

    expect(result).toBeFalse();
    expect(router.navigate).toHaveBeenCalledOnceWith(['/']);
    expect(dispatched.length).toBe(0);
  });

  it('bounces an authenticated user with no profile (apiUser null) to home without triggering login', () => {
    appState$.next(completedState(null, stubAuthUser()));

    let result: boolean | undefined;
    guard.canActivate({ data: { allowedRoles: ['Resident'] } } as any, { url: '/manage' } as any).subscribe(v => (result = v));

    expect(result).toBeFalse();
    expect(router.navigate).toHaveBeenCalledOnceWith(['/']);
    expect(dispatched.length).toBe(0);
  });

  it('routes an unauthenticated visitor through login with the intended URL preserved for return', () => {
    appState$.next({
      ...initialStateValue,
      authSessionResolved: true,
      authBootstrapStatus: 'idle',
      authUser: null,
      apiUser: null,
    });

    let result: boolean | undefined;
    guard
      .canActivate({ data: { allowedRoles: ['Resident'] } } as any, { url: '/manage/approvals?message=abc' } as any)
      .subscribe(v => (result = v));

    expect(result).toBeFalse();
    expect(router.navigate).toHaveBeenCalledOnceWith(['/']);
    expect(dispatched.length).toBe(1);
    expect(dispatched[0] instanceof Login).toBeTrue();
    expect((dispatched[0] as Login).redirectTo).toBe('/manage/approvals?message=abc');
  });

  it('waits for bootstrap to complete before resolving', () => {
    // Start with in-progress bootstrap
    appState$.next({
      ...initialStateValue,
      authSessionResolved: true,
      authBootstrapStatus: 'inProgress',
      apiUser: null,
    });

    let result: boolean | undefined;
    guard.canActivate({ data: { allowedRoles: ['Resident'] } } as any, { url: '/manage' } as any).subscribe(v => (result = v));

    expect(result).toBeUndefined();

    // Now bootstrap completes with a valid user
    appState$.next(completedState(makeApiUser(['Resident'])));

    expect(result).toBeTrue();
  });

  it('routes through login-with-return after timeout if bootstrap never completes', fakeAsync(() => {
    let result: boolean | undefined;
    guard.canActivate({ data: { allowedRoles: ['Resident'] } } as any, { url: '/manage/approvals?message=abc' } as any).subscribe(v => (result = v));

    tick(ROLE_RESOLVE_TIMEOUT_MS - 1);
    expect(result).toBeUndefined();

    tick(1);
    expect(result).toBeFalse();
    expect(router.navigate).toHaveBeenCalledOnceWith(['/']);
    expect(dispatched.length).toBe(1);
    expect(dispatched[0] instanceof Login).toBeTrue();
    expect((dispatched[0] as Login).redirectTo).toBe('/manage/approvals?message=abc');
  }));

  it('denies activation and redirects to /unauthorized when requireResidentRole is true and user lacks Resident role', () => {
    appState$.next(completedState(makeApiUser(['Administrator'])));

    let result: boolean | undefined;
    guard
      .canActivate({ data: { allowedRoles: ['Administrator'], requireResidentRole: true } } as any, { url: '/manage/events' } as any)
      .subscribe(v => (result = v));

    expect(result).toBeFalse();
    expect(router.navigate).toHaveBeenCalledOnceWith(['/unauthorized']);
    expect(dispatched.length).toBe(0);
  });
});
