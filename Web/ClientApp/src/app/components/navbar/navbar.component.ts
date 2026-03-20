import { Component, OnInit, ChangeDetectionStrategy, Inject } from '@angular/core';
import { Observable, Observer } from 'rxjs';
import { map } from 'rxjs/operators';
import { Router, NavigationStart, ActivatedRoute, UrlSegment, NavigationEnd } from '@angular/router';
import { applicationState, ApplicationState, dispatcher, Action, Login, Logout } from 'src/app/state';
import { ApiUser, AuthUser } from 'src/app/models';
import { rolePermissions } from 'src/app/services/rolepermission.service';
import { ThemeService } from 'src/app/services/theme.service';

@Component({
    selector: 'app-navbar',
    templateUrl: './navbar.component.html',
    styleUrls: ['./navbar.component.css'],
    standalone: false
})
export class NavbarComponent implements OnInit {

  disabled = false;
  isNavbarCollapsed = true;
  isHidden: boolean = false;

  constructor(
    @Inject(applicationState) private appState: Observable<ApplicationState>,
    @Inject(dispatcher) private dispatcher: Observer<Action>,
    private router: Router,
    private themeService: ThemeService) {

    router.events.subscribe(e => {
      if (e instanceof NavigationStart) {
        this.isNavbarCollapsed = true;
      }

      if (e instanceof NavigationEnd) {
        if (e.url.startsWith('/rendered')) {
          this.isHidden = true;
        } else {
          this.isHidden = false;
        }
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

  get apiUser$(): Observable<ApiUser | null> {
    return this.appState.pipe(map(s => s.apiUser));
  }

  get authUser$(): Observable<AuthUser | null> {
    return this.appState.pipe(map(s => s.authUser));
  }

  get authBootstrapCompleted$(): Observable<boolean> {
    return this.appState.pipe(map(s => s.authBootstrapStatus === 'completed'));
  }

  get navVm$(): Observable<{ authUser: AuthUser | null; apiUser: ApiUser | null; authBootstrapCompleted: boolean; showAuthenticatedNav: boolean; showGuestPrivacy: boolean }> {
    return this.appState.pipe(map(s => {
      const authBootstrapCompleted = s.authBootstrapStatus === 'completed';
      const showAuthenticatedNav = authBootstrapCompleted && s.apiUser != null;
      return {
        authUser: s.authUser,
        apiUser: s.apiUser,
        authBootstrapCompleted,
        showAuthenticatedNav,
        showGuestPrivacy: !showAuthenticatedNav
      };
    }));
  }

  get manageVisible$(): Observable<boolean> {
    return this.navVm$.pipe(map(vm => vm.showAuthenticatedNav && vm.apiUser !== null && vm.apiUser.roles.filter(r => rolePermissions.manageRoles.includes(r)).length > 0))
  }

  get isDarkTheme$(): Observable<boolean> {
    return this.themeService.isDarkTheme$;
  }

  toggleTheme(): void {
    this.themeService.toggleTheme();
  }

}
