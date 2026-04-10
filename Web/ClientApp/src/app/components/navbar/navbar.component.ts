import { Component, Inject, OnInit } from '@angular/core';
import { Observable, Observer, combineLatest, of } from 'rxjs';
import { catchError, map, shareReplay } from 'rxjs/operators';
import { EventsService } from 'src/app/services/events.service';
import { VendorFlagNotification } from 'src/app/services/vendors.service';
import { HeldMessageNotification, HeldMessageNotificationsService } from 'src/app/services/held-message-notifications.service';
import { Router, NavigationEnd, NavigationStart } from '@angular/router';
import { applicationState, ApplicationState, dispatcher, Action, Login, MockLogin, Logout } from 'src/app/state';
import { ApiUser, AuthUser } from 'src/app/models';
import { rolePermissions } from 'src/app/services/rolepermission.service';
import { ThemeService } from 'src/app/services/theme.service';
import { environment } from 'src/environments/environment';
import { VendorFlagNotificationsService } from 'src/app/services/vendor-flag-notifications.service';

@Component({
  selector: 'app-navbar',
  templateUrl: './navbar.component.html',
  styleUrls: ['./navbar.component.css'],
  standalone: false,
})
export class NavbarComponent implements OnInit {
  disabled = false;
  isNavbarCollapsed = true;
  isHidden = false;
  readonly useMockAuth = environment.useMockAuth;
  readonly mockUsers = [
    { id: 'user-1', label: 'Mock Resident (Admin)' },
    { id: 'user-2', label: 'Taylor Neighbor' },
  ];

  /** True when `GET api/events` returns at least one upcoming event (hides nav link if empty or on error). */
  readonly showEventsNav$: Observable<boolean>;

  readonly vendorFlagNotifications$: Observable<VendorFlagNotification[]>;
  readonly unreadVendorFlagNotificationCount$: Observable<number>;
  readonly heldMessageNotifications$: Observable<HeldMessageNotification[]>;
  readonly unreadHeldMessageCount$: Observable<number>;
  readonly totalUnreadCount$: Observable<number>;

  constructor(
    @Inject(applicationState) private appState: Observable<ApplicationState>,
    @Inject(dispatcher) private dispatcher: Observer<Action>,
    private router: Router,
    private themeService: ThemeService,
    private readonly eventsService: EventsService,
    private readonly vendorFlagNotificationsService: VendorFlagNotificationsService,
    private readonly heldMessageNotificationsService: HeldMessageNotificationsService,
  ) {
    this.showEventsNav$ = this.eventsService.getUpcoming().pipe(
      map(events => events.length > 0),
      catchError(() => of(false)),
      shareReplay({ bufferSize: 1, refCount: true }),
    );

    this.vendorFlagNotifications$ = this.vendorFlagNotificationsService.notifications$;
    this.unreadVendorFlagNotificationCount$ = this.vendorFlagNotificationsService.unreadCount$;
    this.heldMessageNotifications$ = this.heldMessageNotificationsService.notifications$;
    this.unreadHeldMessageCount$ = this.heldMessageNotificationsService.unreadCount$;
    this.totalUnreadCount$ = combineLatest([
      this.vendorFlagNotificationsService.unreadCount$,
      this.heldMessageNotificationsService.unreadCount$,
    ]).pipe(map(([a, b]) => a + b));

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
    this.authUser$.subscribe(() => {
      this.disabled = false;
    });
  }

  login() {
    this.disabled = true;
    this.dispatcher.next(new Login());
  }

  loginAs(userId: string) {
    this.dispatcher.next(new MockLogin(userId));
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

  get navVm$(): Observable<{
    authUser: AuthUser | null;
    apiUser: ApiUser | null;
    authBootstrapCompleted: boolean;
    showAuthenticatedNav: boolean;
    showGuestPrivacy: boolean;
  }> {
    return this.appState.pipe(
      map(s => {
        const authBootstrapCompleted = s.authBootstrapStatus === 'completed';
        const showAuthenticatedNav = authBootstrapCompleted && s.apiUser != null;
        return {
          authUser: s.authUser,
          apiUser: s.apiUser,
          authBootstrapCompleted,
          showAuthenticatedNav,
          showGuestPrivacy: !showAuthenticatedNav,
        };
      }),
    );
  }

  get manageVisible$(): Observable<boolean> {
    return this.navVm$.pipe(
      map(
        vm =>
          vm.showAuthenticatedNav &&
          vm.apiUser !== null &&
          vm.apiUser.roles.filter(r => rolePermissions.manageRoles.includes(r)).length > 0,
      ),
    );
  }

  get adminNotificationsVisible$(): Observable<boolean> {
    return this.navVm$.pipe(map(vm => vm.showAuthenticatedNav && vm.apiUser !== null && vm.apiUser.roles.includes('Administrator')));
  }

  get isDarkTheme$(): Observable<boolean> {
    return this.themeService.isDarkTheme$;
  }

  toggleTheme(): void {
    this.themeService.toggleTheme();
  }

  openFlagNotification(notification: VendorFlagNotification): void {
    this.vendorFlagNotificationsService.markAsRead(notification.flagId);
    this.router.navigate(['/residents/vendors', notification.vendorId]);
  }

  openHeldNotification(notification: HeldMessageNotification): void {
    this.heldMessageNotificationsService.markAsRead(notification.id);
    this.router.navigate(['/manage/committees']);
  }
}
