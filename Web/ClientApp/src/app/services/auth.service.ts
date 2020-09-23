import { Injectable, Inject } from '@angular/core';
import { Observable, Subject } from 'rxjs';
import { environment } from 'src/environments/environment';
import { AuthConfig, OAuthService } from 'angular-oauth2-oidc';
import { IdentityClaims } from '../models';
import { ApplicationState, dispatcher, Action, applicationState, AuthenticatedUserChanged, Login, Logout } from '../state';

@Injectable({ providedIn: 'root' })
export class AuthService {

  constructor(private oauthService: OAuthService,
    @Inject(applicationState) private appState: Observable<ApplicationState>,
    @Inject(dispatcher) private dispatcher: Subject<Action>) {

    const authCodeFlowConfig: AuthConfig = {

      issuer: 'https://cohadorgb2c.b2clogin.com/a7e9006b-c606-4670-960c-3998b35ea5ee/v2.0/',

      tokenEndpoint: 'https://cohadorgb2c.b2clogin.com/cohadorgb2c.onmicrosoft.com/b2c_1_default/oauth2/v2.0/token',

      loginUrl: 'https://cohadorgb2c.b2clogin.com/cohadorgb2c.onmicrosoft.com/b2c_1_default/oauth2/v2.0/authorize',

      logoutUrl: 'https://cohadorgb2c.b2clogin.com/cohadorgb2c.onmicrosoft.com/b2c_1_default/oauth2/v2.0/logout',

      strictDiscoveryDocumentValidation: false,

      // URL of the SPA to redirect the user to after login
      redirectUri: window.location.origin,

      // The SPA's id. The SPA is registerd with this id at the auth-server
      // clientId: 'server.code',
      clientId: '6034a3a8-53b5-401b-a66f-54be5966a067',

      // Just needed if your auth server demands a secret. In general, this
      // is a sign that the auth server is not configured with SPAs in mind
      // and it might not enforce further best practices vital for security
      // such applications.
      dummyClientSecret: '9g~nU-gSmG27VQfevME3-A5qpBWBHsis.X',

      responseType: 'code',

      // set the scope for the permissions the client should request
      // The first four are defined by OIDC.
      // Important: Request offline_access to get a refresh token
      // The api scope is a usecase specific one
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

    this.dispatcher.subscribe(a => {
      if (a instanceof Login) {
        this.oauthService.initCodeFlow();
      } else if (a instanceof Logout) {
        this.oauthService.logOut();
      }
    });

    this.updateState();

    this.oauthService.tryLogin();
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
