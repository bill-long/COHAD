import { HttpClient } from '@angular/common/http';
import { Injectable, Inject } from '@angular/core';
import { Observable, Subject } from 'rxjs';
import { environment } from 'src/environments/environment';
import { AuthConfig, OAuthService } from 'angular-oauth2-oidc';
import { IdentityClaims } from '../models';
import { ApplicationState, dispatcher, Action, applicationState, AuthenticatedUserChanged, Login, Logout } from '../state';
import { MockAuthTokenService } from './mock-auth-token.service';

@Injectable({ providedIn: 'root' })
export class AuthService {

  constructor(
    private oauthService: OAuthService,
    private http: HttpClient,
    private mockTokens: MockAuthTokenService,
    @Inject(applicationState) private appState: Observable<ApplicationState>,
    @Inject(dispatcher) private dispatcher: Subject<Action>) {

    if (!environment.useMockAuth) {
      const authCodeFlowConfig: AuthConfig = {

        issuer: 'https://cohadorgb2c.b2clogin.com/a7e9006b-c606-4670-960c-3998b35ea5ee/v2.0/',

        tokenEndpoint: 'https://cohadorgb2c.b2clogin.com/cohadorgb2c.onmicrosoft.com/b2c_1_default/oauth2/v2.0/token',

        loginUrl: 'https://cohadorgb2c.b2clogin.com/cohadorgb2c.onmicrosoft.com/b2c_1_default/oauth2/v2.0/authorize',

        logoutUrl: 'https://cohadorgb2c.b2clogin.com/cohadorgb2c.onmicrosoft.com/b2c_1_default/oauth2/v2.0/logout',

        strictDiscoveryDocumentValidation: false,

        redirectUri: window.location.origin,

        clientId: '6034a3a8-53b5-401b-a66f-54be5966a067',

        dummyClientSecret: '9g~nU-gSmG27VQfevME3-A5qpBWBHsis.X',

        responseType: 'code',

        scope: 'openid profile email offline_access https://cohadorgb2c.onmicrosoft.com/5803d9fa-a62f-401c-b0f4-269b3cb468eb/API',

        showDebugInformation: (environment.production ? false : true)
      };

      this.oauthService.configure(authCodeFlowConfig);

      this.oauthService.setupAutomaticSilentRefresh();

      this.oauthService.events
        .subscribe(async e => {
          console.log('OAuthService event', e);
          this.updateState();
        });
    }

    this.dispatcher.subscribe(a => {
      if (environment.useMockAuth) {
        if (a instanceof Login) {
          this.initMockAuth();
        } else if (a instanceof Logout) {
          this.mockTokens.setToken(null);
          this.dispatcher.next(new AuthenticatedUserChanged(null));
        }
      } else {
        if (a instanceof Login) {
          this.oauthService.initCodeFlow();
        } else if (a instanceof Logout) {
          this.oauthService.logOut();
        }
      }
    });

    if (!environment.useMockAuth) {
      this.updateState();
      this.oauthService.tryLogin();
    } else {
      this.initMockAuth();
    }
  }

  private initMockAuth(): void {
    this.http.get<{ accessToken: string }>('api/dev/mock-auth').subscribe({
      next: (r) => {
        this.mockTokens.setToken(r.accessToken);
        const identityClaims: IdentityClaims = {
          sub: 'user-1',
          given_name: 'Mock',
          family_name: 'Resident',
          emails: ['mock@cohad.local'],
          idp: 'https://cohad.mock/',
          streetAddress: '123 Mock Lane'
        };
        this.dispatcher.next(new AuthenticatedUserChanged({ identityClaims, accessToken: r.accessToken }));
      },
      error: (err) => {
        console.error('Mock auth unavailable (is the API running with ASPNETCORE_ENVIRONMENT=MockData?)', err);
      }
    });
  }

  private updateState(): void {
    let accessToken = this.oauthService.getAccessToken();
    if (accessToken != null && this.oauthService.getAccessTokenExpiration() < Date.now()) {
      if (!environment.production) {
        console.log('Found expired token. Refreshing.');

      }
      this.oauthService.refreshToken();
      return;
    }

    let identityClaims = this.oauthService.getIdentityClaims() as IdentityClaims;
    this.dispatcher.next(new AuthenticatedUserChanged({ identityClaims, accessToken }));
  }
}
