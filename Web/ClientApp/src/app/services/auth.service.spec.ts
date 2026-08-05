import { BehaviorSubject, Subject } from 'rxjs';
import { AuthService } from './auth.service';
import { Action, AuthenticatedUserChanged, initialStateValue } from '../state';

/**
 * Covers the two things about this service that are dangerous to get wrong: what it hands to the
 * OAuth library, and what it does when a refresh token can no longer be redeemed.
 */
describe('AuthService', () => {
  interface OAuthStub {
    configure: jasmine.Spy;
    setupAutomaticSilentRefresh: jasmine.Spy;
    events: Subject<unknown>;
    tryLogin: jasmine.Spy;
    refreshToken: jasmine.Spy;
    logOut: jasmine.Spy;
    getAccessToken: () => string | null;
    getAccessTokenExpiration: () => number;
    getIdentityClaims: () => unknown;
    initCodeFlow: jasmine.Spy;
  }

  /** Token state the stub reads, so logOut() can realistically clear it the way the library does. */
  let storedToken: string | null;
  let expiresAt: number;

  function makeOAuth(refreshResult: () => Promise<unknown>): OAuthStub {
    return {
      configure: jasmine.createSpy('configure'),
      setupAutomaticSilentRefresh: jasmine.createSpy('setupAutomaticSilentRefresh'),
      events: new Subject<unknown>(),
      tryLogin: jasmine.createSpy('tryLogin').and.returnValue(Promise.resolve(true)),
      refreshToken: jasmine.createSpy('refreshToken').and.callFake(refreshResult),
      logOut: jasmine.createSpy('logOut').and.callFake(() => {
        storedToken = null;
      }),
      getAccessToken: () => storedToken,
      getAccessTokenExpiration: () => expiresAt,
      getIdentityClaims: () => ({ sub: 'user-1' }),
      initCodeFlow: jasmine.createSpy('initCodeFlow'),
    };
  }

  function construct(oauth: OAuthStub, dispatcher: Subject<Action>): AuthService {
    return new AuthService(
      oauth as never,
      {} as never,
      { setToken: () => undefined } as never,
      { navigateByUrl: () => undefined } as never,
      { setAuthenticatedUser: () => undefined, clearAuthenticatedUser: () => undefined, flush: () => undefined } as never,
      new BehaviorSubject(initialStateValue) as never,
      dispatcher,
    );
  }

  /** Lets the constructor's tryLogin().then chain and the refresh rejection settle. */
  async function settle(): Promise<void> {
    for (let i = 0; i < 5; i++) {
      await Promise.resolve();
    }
  }

  beforeEach(() => {
    storedToken = null;
    expiresAt = Date.now() + 60_000;
  });

  it('hands the OAuth library a config carrying no credential', () => {
    // The config factory has its own guard, but the invariant is about what actually reaches
    // configure(): spreading an extra property in at this call site would defeat that guard while
    // leaving every spec in auth-code-flow.config.spec.ts green.
    const oauth = makeOAuth(() => Promise.resolve());

    construct(oauth, new Subject<Action>());

    expect(oauth.configure).toHaveBeenCalledTimes(1);
    const passed = oauth.configure.calls.mostRecent().args[0] as Record<string, unknown>;
    expect(passed['dummyClientSecret']).toBeUndefined();
    expect(passed['customQueryParams']).toBeUndefined();
  });

  it('clears the session and reports signed out when the refresh token cannot be redeemed', async () => {
    // The case this exists for: changing clientId invalidates every refresh token already issued,
    // so on the first deploy every open session takes this path at once. Leaving the stale token in
    // place would keep the UI showing a signed-in user whose every API call returns 401.
    storedToken = 'expired-token';
    expiresAt = Date.now() - 1;
    const dispatched: Action[] = [];
    const dispatcher = new Subject<Action>();
    dispatcher.subscribe(a => dispatched.push(a));
    const oauth = makeOAuth(() => Promise.reject(new Error('invalid_grant')));

    construct(oauth, dispatcher);
    await settle();

    expect(oauth.logOut).toHaveBeenCalledWith(true);
    const signedOut = dispatched.filter(a => a instanceof AuthenticatedUserChanged) as AuthenticatedUserChanged[];
    expect(signedOut.length).toBeGreaterThan(0);
    expect(signedOut[signedOut.length - 1].authUser).toBeNull();
  });

  it('does not retry a failed refresh when the library reports the failure as an event', async () => {
    // refreshToken() rejecting also emits an OAuth error event, and the constructor routes every
    // event back through updateState(). Without the in-flight guard plus the token clear, that path
    // re-enters on its own failure and hammers the token endpoint for as long as the tab is open.
    storedToken = 'expired-token';
    expiresAt = Date.now() - 1;
    const oauth = makeOAuth(() => Promise.reject(new Error('invalid_grant')));

    construct(oauth, new Subject<Action>());
    await settle();
    const afterFirstFailure = oauth.refreshToken.calls.count();

    oauth.events.next({ type: 'token_refresh_error' });
    await settle();

    expect(afterFirstFailure).toBe(1);
    expect(oauth.refreshToken.calls.count()).toBe(1);
  });
});
