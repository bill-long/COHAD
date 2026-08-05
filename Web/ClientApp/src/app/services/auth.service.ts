import { HttpClient } from '@angular/common/http';
import { Injectable, Inject } from '@angular/core';
import { Router } from '@angular/router';
import { Observable, Subject } from 'rxjs';
import { environment } from 'src/environments/environment';
import { OAuthService } from 'angular-oauth2-oidc';
import { IdentityClaims } from '../models';
import {
  ApplicationState,
  dispatcher,
  Action,
  applicationState,
  AuthenticatedUserChanged,
  AuthSessionResolved,
  Login,
  MockLogin,
  Logout,
} from '../state';
import { buildAuthCodeFlowConfig } from './auth-code-flow.config';
import { MockAuthTokenService } from './mock-auth-token.service';
import { ApplicationInsightsService } from './application-insights.service';

@Injectable({ providedIn: 'root' })
export class AuthService {
  private authSessionResolvedDispatched = false;
  /** Guards against a failing refresh re-entering through the OAuth error event it raises. */
  private refreshInFlight = false;
  private static readonly postLoginRedirectKey = 'auth.postLoginRedirect';
  private static readonly defaultPostLoginPath = '/residents';

  /** App-internal path only: must start with `/` and not `//` (blocks protocol-relative and open redirects). */
  private sanitizePostLoginRedirect(redirectTo: string | undefined): string {
    const t = (redirectTo ?? '').trim();
    if (!t) {
      return AuthService.defaultPostLoginPath;
    }
    if (!t.startsWith('/') || t.startsWith('//')) {
      return AuthService.defaultPostLoginPath;
    }
    return t;
  }

  constructor(
    private oauthService: OAuthService,
    private http: HttpClient,
    private mockTokens: MockAuthTokenService,
    private router: Router,
    private telemetry: ApplicationInsightsService,
    @Inject(applicationState) private appState: Observable<ApplicationState>,
    @Inject(dispatcher) private dispatcher: Subject<Action>,
  ) {
    if (!environment.useMockAuth) {
      this.oauthService.configure(buildAuthCodeFlowConfig());

      this.oauthService.setupAutomaticSilentRefresh();

      this.oauthService.events.subscribe(async e => {
        if (!environment.production) {
          console.log('OAuthService event', e);
        }
        if (this.updateState()) {
          this.markAuthSessionResolvedOnce();
        }
      });
    }

    this.dispatcher.subscribe(a => {
      if (environment.useMockAuth) {
        if (a instanceof MockLogin) {
          this.markPostLoginRedirect(a.redirectTo);
          this.initMockAuth(a.userId);
        } else if (a instanceof Logout) {
          this.mockTokens.setToken(null);
          this.dispatcher.next(new AuthenticatedUserChanged(null));
          this.telemetry.clearAuthenticatedUser();
          this.telemetry.flush();
        }
      } else {
        if (a instanceof Login) {
          this.markPostLoginRedirect(a.redirectTo);
          this.oauthService.initCodeFlow();
        } else if (a instanceof Logout) {
          this.telemetry.clearAuthenticatedUser();
          this.telemetry.flush();
          this.oauthService.logOut();
        }
      }
    });

    if (!environment.useMockAuth) {
      this.oauthService
        .tryLogin()
        .then(() => {
          if (this.updateState()) {
            this.markAuthSessionResolvedOnce();
          }
        })
        .catch(() => {
          this.updateState();
          this.markAuthSessionResolvedOnce();
        });
    } else {
      // In mock mode, don't auto-login — wait for the user to select a mock user.
      this.markAuthSessionResolvedOnce();
    }
  }

  private markAuthSessionResolvedOnce(): void {
    if (this.authSessionResolvedDispatched) {
      return;
    }
    this.authSessionResolvedDispatched = true;
    this.dispatcher.next(new AuthSessionResolved());
  }

  private readonly mockUserClaims: Record<string, Omit<IdentityClaims, 'idp'>> = {
    'user-1': { sub: 'user-1', given_name: 'Mock', family_name: 'Resident', emails: ['mock@cohad.local'], streetAddress: '123 Mock Lane' },
    'user-2': {
      sub: 'user-2',
      given_name: 'Taylor',
      family_name: 'Neighbor',
      emails: ['taylor@cohad.local'],
      streetAddress: '456 Test Court',
    },
  };

  private initMockAuth(userId: string): void {
    this.http.get<{ accessToken: string }>(`api/dev/mock-auth?userId=${encodeURIComponent(userId)}`).subscribe({
      next: r => {
        this.mockTokens.setToken(r.accessToken);
        const claims = this.mockUserClaims[userId] ?? this.mockUserClaims['user-1'];
        const identityClaims: IdentityClaims = { ...claims, idp: 'https://cohad.mock/' };
        this.dispatcher.next(new AuthenticatedUserChanged({ identityClaims, accessToken: r.accessToken }));
        this.telemetry.setAuthenticatedUser(identityClaims.sub);
        this.redirectAfterLoginIfRequested(r.accessToken);
        this.markAuthSessionResolvedOnce();
      },
      error: err => {
        console.error('Mock auth unavailable (is the API running with ASPNETCORE_ENVIRONMENT=MockData?)', err);
        this.markAuthSessionResolvedOnce();
      },
    });
  }

  /** @returns true if AuthenticatedUserChanged was dispatched (false when refresh was started and dispatch is deferred). */
  private updateState(): boolean {
    const accessToken = this.oauthService.getAccessToken();
    if (accessToken != null && this.oauthService.getAccessTokenExpiration() < Date.now()) {
      if (!environment.production) {
        console.log('Found expired token. Refreshing.');
      }

      // A failing refresh emits an OAuth error event, and the events subscription routes that back
      // here - so without this guard the retry re-enters on its own failure and hammers the token
      // endpoint until the tab closes.
      if (this.refreshInFlight) {
        return false;
      }
      this.refreshInFlight = true;

      try {
        // Typed as possibly absent rather than as the declared Promise: a stubbed OAuthService can
        // return nothing, and the in-flight flag must still be cleared when it does.
        const refreshResult: Promise<unknown> | undefined = this.oauthService.refreshToken();

        // Handle refresh errors explicitly so that the auth session is resolved even on failure.
        if (refreshResult && typeof refreshResult.then === 'function') {
          refreshResult.then(
            () => {
              this.refreshInFlight = false;
            },
            err => {
              this.refreshInFlight = false;
              this.abandonUnrefreshableSession(err);
            },
          );
        } else {
          this.refreshInFlight = false;
        }
      } catch (err) {
        this.refreshInFlight = false;
        this.abandonUnrefreshableSession(err);
      }
      return false;
    }

    if (accessToken) {
      const identityClaims = this.oauthService.getIdentityClaims() as IdentityClaims;
      this.dispatcher.next(new AuthenticatedUserChanged({ identityClaims, accessToken }));
      this.telemetry.setAuthenticatedUser(identityClaims.sub);
      this.redirectAfterLoginIfRequested(accessToken);
    } else {
      this.dispatcher.next(new AuthenticatedUserChanged(null));
      this.telemetry.clearAuthenticatedUser();
    }
    return true;
  }

  /**
   * Drops a session whose refresh token can no longer be redeemed.
   *
   * Leaving the stored tokens in place is the dangerous option: the expired access token keeps
   * `updateState` reporting a signed-in user, so the header renders as logged in while every API
   * call returns 401, and nothing ever tells the person why. Clearing them locally makes the next
   * pass fall through to the signed-out branch, which both fixes the display and stops the retry.
   *
   * Deliberately a local sign-out (`logOut(true)`): a redirect to the B2C logout URL would throw
   * the user out of whatever page they were on to fix a failure they did not cause.
   */
  private abandonUnrefreshableSession(err: unknown): void {
    console.error('Token refresh failed; clearing the local session.', err);
    try {
      this.oauthService.logOut(true);
    } catch (logoutErr) {
      // Storage may already be unusable; the dispatch below is what the UI actually reads.
      console.error('Clearing the local session failed.', logoutErr);
    }
    this.dispatcher.next(new AuthenticatedUserChanged(null));
    this.telemetry.clearAuthenticatedUser();
    this.markAuthSessionResolvedOnce();
  }

  private markPostLoginRedirect(redirectTo?: string): void {
    const target = this.sanitizePostLoginRedirect(redirectTo);
    try {
      sessionStorage.setItem(AuthService.postLoginRedirectKey, target);
    } catch {
      // Ignore storage failures.
    }
  }

  private redirectAfterLoginIfRequested(accessToken: string | null | undefined): void {
    if (!accessToken) {
      return;
    }

    let redirectTo: string | null = null;
    try {
      redirectTo = sessionStorage.getItem(AuthService.postLoginRedirectKey);
      if (redirectTo) {
        sessionStorage.removeItem(AuthService.postLoginRedirectKey);
      }
    } catch {
      redirectTo = null;
    }

    if (redirectTo) {
      this.router.navigateByUrl(this.sanitizePostLoginRedirect(redirectTo));
    }
  }
}
