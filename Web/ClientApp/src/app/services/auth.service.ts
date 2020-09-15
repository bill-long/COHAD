import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { UserManager, User, UserManagerSettings, Log } from 'oidc-client';
import { Observable, from, Subject, BehaviorSubject } from 'rxjs';
import { map, shareReplay } from 'rxjs/operators';
import { environment } from 'src/environments/environment';
import { Router } from '@angular/router';

@Injectable({
  providedIn: 'root'
})
export class AuthService {

  public user$: Observable<User>;
  public accessToken$: Observable<string>;

  private userManager: UserManager;
  private userSubject: Subject<User>;

  constructor(private httpClient: HttpClient, private router: Router) {
    if (!environment.production) {
      Log.logger = console;
    }

    var config: UserManagerSettings = {
      authority: 'https://cohad.b2clogin.com/cohad.onmicrosoft.com/v2.0',
      client_id: 'f172253e-dc5f-4429-818f-fc506f6bf4a6',
      redirect_uri: window.location.origin + '/authcallback.html',
      scope: 'openid profile https://cohad.onmicrosoft.com/frontend/api',
      response_type: 'id_token token',
      post_logout_redirect_uri: window.location.origin,
      extraQueryParams: {'p': 'b2c_1_v2_signup_signin'},
      metadataUrl: 'https://cohad.b2clogin.com/cohad.onmicrosoft.com/v2.0/.well-known/openid-configuration?p=b2c_1_v2_signup_signin',
      loadUserInfo: false, // Because Azure B2C doesn't currently support userinfo_endpoint
      automaticSilentRenew: true,
      silent_redirect_uri: window.location.origin + '/authsilentrenew.html'
    };

    this.userManager = new UserManager(config);
    this.userSubject = new BehaviorSubject<User>(null);
    this.user$ = this.userSubject.asObservable();
    this.accessToken$ = this.user$.pipe(map(u => {
      if (u && u.access_token) return u.access_token;
      return null;
    }));

    this.userManager.getUser().then(u => {
      if (u && !u.expired) {
        this.userSubject.next(u);
      }
    });

    this.userManager.events.addUserLoaded(() => {
      this.userManager.getUser().then(u => this.userSubject.next(u));
    });

    this.userManager.events.addAccessTokenExpired(() => this.userSubject.next(null));
  }

  login(): Promise<any> {
    return this.userManager.signinRedirect();
  }

  logout(): Promise<any> {
    return this.userManager.signoutRedirect();
  }

  async processCallback() {
      let u = await this.userManager.signinRedirectCallback();
      this.userSubject.next(u);
      return u;
  }

}
