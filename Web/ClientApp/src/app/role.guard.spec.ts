import { TestBed, fakeAsync, tick } from '@angular/core/testing';
import { BehaviorSubject } from 'rxjs';
import { Router } from '@angular/router';
import { RoleGuard, ROLE_RESOLVE_TIMEOUT_MS } from './role.guard';
import { ApplicationState, applicationState, initialStateValue } from './state';
import { ApiUser } from './models';

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

function completedState(apiUser: ApiUser | null): ApplicationState {
  return {
    ...initialStateValue,
    authSessionResolved: true,
    authBootstrapStatus: 'completed',
    apiUser,
  };
}

describe('RoleGuard', () => {
  let appState$: BehaviorSubject<ApplicationState>;
  let guard: RoleGuard;
  let router: jasmine.SpyObj<Router>;

  beforeEach(() => {
    appState$ = new BehaviorSubject<ApplicationState>(initialStateValue);
    router = jasmine.createSpyObj<Router>('Router', ['navigate']);
    router.navigate.and.resolveTo(true);

    TestBed.configureTestingModule({
      providers: [RoleGuard, { provide: applicationState, useValue: appState$ }, { provide: Router, useValue: router }],
    });

    guard = TestBed.inject(RoleGuard);
  });

  it('allows activation when user has a required role', () => {
    appState$.next(completedState(makeApiUser(['Resident'])));

    let result: boolean | undefined;
    guard.canActivate({ data: { allowedRoles: ['Resident', 'Administrator'] } } as any, {} as any).subscribe(v => (result = v));

    expect(result).toBeTrue();
    expect(router.navigate).not.toHaveBeenCalled();
  });

  it('denies activation when user lacks required role', () => {
    appState$.next(completedState(makeApiUser(['WelcomeCommittee'])));

    let result: boolean | undefined;
    guard.canActivate({ data: { allowedRoles: ['Resident', 'Administrator'] } } as any, {} as any).subscribe(v => (result = v));

    expect(result).toBeFalse();
    expect(router.navigate).toHaveBeenCalledOnceWith(['/']);
  });

  it('denies activation when apiUser is null after bootstrap completes', () => {
    appState$.next(completedState(null));

    let result: boolean | undefined;
    guard.canActivate({ data: { allowedRoles: ['Resident'] } } as any, {} as any).subscribe(v => (result = v));

    expect(result).toBeFalse();
    expect(router.navigate).toHaveBeenCalledOnceWith(['/']);
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
    guard.canActivate({ data: { allowedRoles: ['Resident'] } } as any, {} as any).subscribe(v => (result = v));

    expect(result).toBeUndefined();

    // Now bootstrap completes with a valid user
    appState$.next(completedState(makeApiUser(['Resident'])));

    expect(result).toBeTrue();
  });

  it('denies activation after timeout if bootstrap never completes', fakeAsync(() => {
    let result: boolean | undefined;
    guard.canActivate({ data: { allowedRoles: ['Resident'] } } as any, {} as any).subscribe(v => (result = v));

    tick(ROLE_RESOLVE_TIMEOUT_MS - 1);
    expect(result).toBeUndefined();

    tick(1);
    expect(result).toBeFalse();
    expect(router.navigate).toHaveBeenCalledOnceWith(['/']);
  }));

  it('denies immediately when auth resolved unauthenticated (bootstrap idle)', () => {
    appState$.next({
      ...initialStateValue,
      authSessionResolved: true,
      authBootstrapStatus: 'idle',
      apiUser: null,
    });

    let result: boolean | undefined;
    guard.canActivate({ data: { allowedRoles: ['Resident'] } } as any, {} as any).subscribe(v => (result = v));

    expect(result).toBeFalse();
    expect(router.navigate).toHaveBeenCalledOnceWith(['/']);
  });

  it('denies activation and redirects to /unauthorized when requireResidentRole is true and user lacks Resident role', () => {
    appState$.next(completedState(makeApiUser(['Administrator'])));

    let result: boolean | undefined;
    guard
      .canActivate({ data: { allowedRoles: ['Administrator'], requireResidentRole: true } } as any, {} as any)
      .subscribe(v => (result = v));

    expect(result).toBeFalse();
    expect(router.navigate).toHaveBeenCalledOnceWith(['/unauthorized']);
  });
});
