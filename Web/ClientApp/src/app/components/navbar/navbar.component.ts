import { Component, OnInit, ChangeDetectionStrategy, Inject } from '@angular/core';
import { Observable, Observer } from 'rxjs';
import { map } from 'rxjs/operators';
import { Router, NavigationStart } from '@angular/router';
import { applicationState, ApplicationState, dispatcher, Action, Login, Logout } from 'src/app/state';
import { AuthService } from 'src/app/services/auth.service';

@Component({
  selector: 'app-navbar',
  templateUrl: './navbar.component.html',
  styleUrls: ['./navbar.component.css'],
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class NavbarComponent implements OnInit {

  disabled = false;
  isNavbarCollapsed = true;

  constructor(
    @Inject(applicationState) private appState: Observable<ApplicationState>,
    @Inject(dispatcher) private dispatcher: Observer<Action>,
    private router: Router) {

    router.events.subscribe(e => {
      if (e instanceof NavigationStart) {
        this.isNavbarCollapsed = true;
      }
    });
  }

  ngOnInit() {
  }

  login() {
    this.disabled = true;
    this.dispatcher.next(new Login());
  }

  logout() {
    this.disabled = true;
    this.dispatcher.next(new Logout());
  }

  get userName$(): Observable<string> {
    return this.appState.pipe(map(s => s.authUser), map(u => {
      if (u && u.identityClaims) {
        return (u.identityClaims.given_name || u.identityClaims.family_name || u.identityClaims.emails[0]);
      }
    }));
  }

}
