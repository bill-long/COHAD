import { Subject } from 'rxjs';
import { ApiUser, AuthUser } from './models';
import { Action, applicationStateFactory, AuthenticatedUserChanged, initialStateValue, LoadUserCompleted, ApplicationState } from './state';

describe('application state auth bootstrap', () => {
  function createApiUser(): ApiUser {
    return {
      uniqueId: 'u-1',
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
      roles: ['Resident'],
      ownedHomes: [],
    };
  }

  function createAuthUser(): AuthUser {
    return {
      accessToken: 'token-1',
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

  it('sets inProgress and clears stale apiUser on auth change', () => {
    const dispatcher = new Subject<Action>();
    const state$ = applicationStateFactory(initialStateValue, dispatcher);
    let latestState: ApplicationState = initialStateValue;
    state$.subscribe(s => (latestState = s));

    dispatcher.next(new LoadUserCompleted(createApiUser()));
    dispatcher.next(new AuthenticatedUserChanged(createAuthUser()));

    expect(latestState.authBootstrapStatus).toBe('inProgress');
    expect(latestState.apiUser).toBeNull();
  });

  it('sets completed after LoadUserCompleted when authenticated', () => {
    const dispatcher = new Subject<Action>();
    const state$ = applicationStateFactory(initialStateValue, dispatcher);
    let latestState: ApplicationState = initialStateValue;
    state$.subscribe(s => (latestState = s));

    dispatcher.next(new AuthenticatedUserChanged(createAuthUser()));
    dispatcher.next(new LoadUserCompleted(createApiUser()));

    expect(latestState.authBootstrapStatus).toBe('completed');
    expect(latestState.apiUser?.uniqueId).toBe('u-1');
  });

  it('preserves apiUser and authBootstrapStatus during same-principal token refresh', () => {
    const dispatcher = new Subject<Action>();
    const state$ = applicationStateFactory(initialStateValue, dispatcher);
    let latestState: ApplicationState = initialStateValue;
    state$.subscribe(s => (latestState = s));

    // Initial login
    dispatcher.next(new AuthenticatedUserChanged(createAuthUser()));
    dispatcher.next(new LoadUserCompleted(createApiUser()));
    expect(latestState.authBootstrapStatus).toBe('completed');
    expect(latestState.apiUser?.uniqueId).toBe('u-1');

    // Token refresh (same sub, different token)
    const refreshedAuth = createAuthUser();
    refreshedAuth.accessToken = 'token-2';
    dispatcher.next(new AuthenticatedUserChanged(refreshedAuth));

    expect(latestState.authBootstrapStatus).toBe('completed');
    expect(latestState.apiUser?.uniqueId).toBe('u-1');
  });

  it('clears apiUser and sets inProgress when principal changes', () => {
    const dispatcher = new Subject<Action>();
    const state$ = applicationStateFactory(initialStateValue, dispatcher);
    let latestState: ApplicationState = initialStateValue;
    state$.subscribe(s => (latestState = s));

    // Initial login
    dispatcher.next(new AuthenticatedUserChanged(createAuthUser()));
    dispatcher.next(new LoadUserCompleted(createApiUser()));
    expect(latestState.authBootstrapStatus).toBe('completed');

    // Different user signs in (different sub)
    const differentUser: AuthUser = {
      accessToken: 'token-different',
      identityClaims: {
        emails: ['other@example.com'],
        family_name: 'Other',
        given_name: 'User',
        idp: 'idp',
        sub: 'different-sub',
        streetAddress: '',
      },
    };
    dispatcher.next(new AuthenticatedUserChanged(differentUser));

    expect(latestState.authBootstrapStatus).toBe('inProgress');
    expect(latestState.apiUser).toBeNull();
  });

  it('returns to idle and clears apiUser on sign out', () => {
    const dispatcher = new Subject<Action>();
    const state$ = applicationStateFactory(initialStateValue, dispatcher);
    let latestState: ApplicationState = initialStateValue;
    state$.subscribe(s => (latestState = s));

    dispatcher.next(new AuthenticatedUserChanged(createAuthUser()));
    dispatcher.next(new LoadUserCompleted(createApiUser()));
    dispatcher.next(new AuthenticatedUserChanged(null));

    expect(latestState.authBootstrapStatus).toBe('idle');
    expect(latestState.apiUser).toBeNull();
  });
});
