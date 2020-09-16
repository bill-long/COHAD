import { Injectable } from '@angular/core';
import { Observable, Subject, BehaviorSubject } from 'rxjs';
import { filter } from 'rxjs/operators';
import { environment } from 'src/environments/environment';
import { AuthConfig, OAuthService } from 'angular-oauth2-oidc';
import { ApiUser, IdentityClaims } from '../models';

@Injectable({ providedIn: 'root' })
export class AuthService {

  public user$: Observable<IdentityClaims>;

  public accessToken$: Observable<string>;

  private userSubject: Subject<IdentityClaims>;

  private accessTokenSubject: Subject<string>;

  constructor(private oauthService: OAuthService) {
    if (!environment.production) {
    }

    this.userSubject = new BehaviorSubject<IdentityClaims>(null);

    this.user$ = this.userSubject.asObservable();

    this.accessTokenSubject = new BehaviorSubject<string>(null);

    this.accessToken$ = this.accessTokenSubject.asObservable();

    const authCodeFlowConfig: AuthConfig = {
      // Url of the Identity Provider
      issuer: 'https://cohadorgb2c.b2clogin.com/a7e9006b-c606-4670-960c-3998b35ea5ee/v2.0/',

      tokenEndpoint: 'https://cohadorgb2c.b2clogin.com/cohadorgb2c.onmicrosoft.com/b2c_1_default/oauth2/v2.0/token',

      loginUrl: 'https://cohadorgb2c.b2clogin.com/cohadorgb2c.onmicrosoft.com/b2c_1_default/oauth2/v2.0/authorize',

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
      // dummyClientSecret: 'secret',

      responseType: 'code',

      // set the scope for the permissions the client should request
      // The first four are defined by OIDC.
      // Important: Request offline_access to get a refresh token
      // The api scope is a usecase specific one
      scope: 'openid profile email offline_access https://cohadorgb2c.onmicrosoft.com/5803d9fa-a62f-401c-b0f4-269b3cb468eb/API',

      showDebugInformation: true,

      dummyClientSecret: '9g~nU-gSmG27VQfevME3-A5qpBWBHsis.X'
    };

    this.oauthService.configure(authCodeFlowConfig);

    this.oauthService.events.subscribe(e => console.log('OAuthService event', e));

    this.oauthService.events
      .subscribe(async e => {
        console.log('OAuthService event', e);
        this.updateSubjects();
      });

    this.updateSubjects();

    this.oauthService.tryLogin();
  }

  login(): void {
    return this.oauthService.initCodeFlow();
  }

  logout(): void {
    this.oauthService.logOut();
  }

  private updateSubjects(): void {
    this.accessTokenSubject.next(this.oauthService.getAccessToken());
    this.userSubject.next(this.oauthService.getIdentityClaims() as IdentityClaims);
  }
}
