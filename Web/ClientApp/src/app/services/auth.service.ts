import { HttpClient } from '@angular/common/http';
import { Injectable, Inject } from '@angular/core';
import { Router } from '@angular/router';
import { Observable, Subject } from 'rxjs';
import { environment } from 'src/environments/environment';
import { AuthConfig, OAuthService } from 'angular-oauth2-oidc';
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
import { MockAuthTokenService } from './mock-auth-token.service';
import { ApplicationInsightsService } from './application-insights.service';

@Injectable({ providedIn: 'root' })
export class AuthService {
  private authSessionResolvedDispatched = false;
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
      const authCodeFlowConfig: AuthConfig = {
        issuer: 'https://cohadorgb2c.b2clogin.com/a7e9006b-c606-4670-960c-3998b35ea5ee/v2.0/',

        tokenEndpoint: 'https://cohadorgb2c.b2clogin.com/cohadorgb2c.onmicrosoft.com/b2c_1_default/oauth2/v2.0/token',

        loginUrl: 'https://cohadorgb2c.b2clogin.com/cohadorgb2c.onmicrosoft.com/b2c_1_default/oauth2/v2.0/authorize',

        logoutUrl: 'https://cohadorgb2c.b2clogin.com/cohadorgb2c.onmicrosoft.com/b2c_1_default/oauth2/v2.0/logout',

        strictDiscoveryDocumentValidation: false,

        redirectUri: window.location.origin,

        clientId: '66d25d05-4ece-4b61-a40d-a16b2fe0adbd',

        dummyClientSecret: '***REMOVED***',

        responseType: 'code',

        scope: 'openid profile email offline_access https://cohadorgb2c.onmicrosoft.com/5803d9fa-a62f-401c-b0f4-269b3cb468eb/API',

        showDebugInformation: environment.production ? false : true,
      };

      this.oauthService.configure(authCodeFlowConfig);

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

      try {
        const refreshResult: any = this.oauthService.refreshToken();

        // Handle refresh errors explicitly so that the auth session is resolved even on failure.
        if (refreshResult && typeof refreshResult.catch === 'function') {
          (refreshResult as Promise<unknown>).catch(err => {
            console.error('Token refresh failed.', err);
            this.markAuthSessionResolvedOnce();
          });
        }
      } catch (err) {
        console.error('Token refresh threw an error.', err);
        this.markAuthSessionResolvedOnce();
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
