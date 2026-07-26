import { Component, Inject, OnInit, QueryList, ViewChildren } from '@angular/core';
import { MatMenuTrigger } from '@angular/material/menu';
import { MatSnackBar, MatSnackBarRef, TextOnlySnackBar } from '@angular/material/snack-bar';
import { Observable, Observer, of } from 'rxjs';
import { catchError, map, shareReplay } from 'rxjs/operators';
import { EventsService } from 'src/app/services/events.service';
import { AppNotification, NotificationsService, NotificationType } from 'src/app/services/notifications.service';
import { Router, NavigationEnd, NavigationStart } from '@angular/router';
import { applicationState, ApplicationState, dispatcher, Action, Login, MockLogin, Logout } from 'src/app/state';
import { ApiUser, AuthUser } from 'src/app/models';
import { rolePermissions } from 'src/app/services/rolepermission.service';
import { ThemeService } from 'src/app/services/theme.service';
import { environment } from 'src/environments/environment';

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

  @ViewChildren(MatMenuTrigger) private menuTriggers?: QueryList<MatMenuTrigger>;

  /** Notifications acknowledged while the current undo snackbar is visible; "Undo" restores all of them. */
  private pendingUndo: AppNotification[] = [];
  private activeAckSnackBar: MatSnackBarRef<TextOnlySnackBar> | null = null;
  readonly useMockAuth = environment.useMockAuth;
  readonly mockUsers = [
    { id: 'user-1', label: 'Mock Resident (Admin)' },
    { id: 'user-2', label: 'Taylor Neighbor' },
  ];

  /** True when `GET api/events` returns at least one upcoming event (hides nav link if empty or on error). */
  readonly showEventsNav$: Observable<boolean>;

  readonly notifications$: Observable<AppNotification[]>;
  readonly unreadCount$: Observable<number>;

  // View-model + visibility streams are built once (shared) so the template's `| async` usages
  // don't recreate them on every change-detection pass.
  readonly navVm$: Observable<{
    authUser: AuthUser | null;
    apiUser: ApiUser | null;
    authBootstrapCompleted: boolean;
    showAuthenticatedNav: boolean;
    showGuestPrivacy: boolean;
  }>;
  readonly manageVisible$: Observable<boolean>;
  /** The bell shows for anyone who can hold a notification audience: Administrators and committee moderators. */
  readonly notificationBellVisible$: Observable<boolean>;

  constructor(
    @Inject(applicationState) private appState: Observable<ApplicationState>,
    @Inject(dispatcher) private dispatcher: Observer<Action>,
    private router: Router,
    private themeService: ThemeService,
    private readonly eventsService: EventsService,
    private readonly notificationsService: NotificationsService,
    private readonly snackBar: MatSnackBar,
  ) {
    this.showEventsNav$ = this.eventsService.getUpcoming().pipe(
      map(events => events.length > 0),
      catchError(() => of(false)),
      shareReplay({ bufferSize: 1, refCount: true }),
    );

    this.notifications$ = this.notificationsService.notifications$;
    this.unreadCount$ = this.notificationsService.unreadCount$;

    this.navVm$ = this.appState.pipe(
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
      shareReplay({ bufferSize: 1, refCount: true }),
    );
    this.manageVisible$ = this.navVm$.pipe(
      map(
        vm =>
          vm.showAuthenticatedNav &&
          vm.apiUser !== null &&
          vm.apiUser.roles.filter(r => rolePermissions.manageRoles.includes(r)).length > 0,
      ),
    );
    this.notificationBellVisible$ = this.navVm$.pipe(
      map(
        vm =>
          vm.showAuthenticatedNav &&
          vm.apiUser !== null &&
          vm.apiUser.roles.some(r => rolePermissions.manageCommitteesRoles.includes(r)),
      ),
    );

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

  get isDarkTheme$(): Observable<boolean> {
    return this.themeService.isDarkTheme$;
  }

  toggleTheme(): void {
    this.themeService.toggleTheme();
  }

  /**
   * Opens the moderation UI that resolves a notification. Prefers the server-computed deepLink (which
   * carries the target's parent context, e.g. a flagged vendor's id); falls back to a type-based route
   * for legacy notifications raised before deepLink existed.
   */
  openNotification(notification: AppNotification): void {
    const route = NavbarComponent.routeFor(notification);
    if (route) {
      this.router.navigateByUrl(route);
    }
  }

  private static routeFor(notification: AppNotification): string | null {
    // Held-message notifications now resolve in the Approvals inbox. A notification raised before that
    // move has a stale stored deepLink ('/manage/committees', which no longer hosts moderation), so
    // derive the route from the target (held-message) id rather than trusting the persisted deepLink.
    if (notification.type === 'HeldMessage') {
      return notification.targetId
        ? `/manage/approvals?message=${encodeURIComponent(notification.targetId)}`
        : '/manage/approvals';
    }
    return notification.deepLink || NavbarComponent.fallbackRoute(notification.type);
  }

  /**
   * Acknowledges a notification that has no other resolving action (registrations). Acknowledging
   * resolves the item for the whole audience with no confirmation, so a brief undo is offered via
   * snackbar instead of an up-front dialog (which would reintroduce per-item friction). Rapid
   * acknowledgements accumulate into a single snackbar ("N marked as handled") whose Undo restores
   * all of them — MatSnackBar shows one snackbar at a time, so a replacement must not orphan the
   * earlier items' only undo entry point.
   */
  acknowledge(notification: AppNotification, source?: EventTarget | null): void {
    const panel = source instanceof HTMLElement ? source.closest<HTMLElement>('.mat-mdc-menu-panel') : null;
    this.notificationsService.acknowledge(notification.id).subscribe({
      next: () => {
        this.offerUndo(notification);
        this.restoreMenuFocus(panel);
      },
      error: () => {
        // Best-effort from the bell; the badge reconciles on the next refresh/signal.
      },
    });
  }

  private offerUndo(notification: AppNotification): void {
    this.pendingUndo.push(notification);
    const count = this.pendingUndo.length;
    // Mark the previous snackbar superseded BEFORE open() dismisses it: its afterDismissed may fire
    // synchronously during open(), and it must not clear the stack the replacement now owns.
    this.activeAckSnackBar = null;
    const ref = this.snackBar.open(count === 1 ? 'Marked as handled' : `${count} marked as handled`, 'Undo', {
      duration: 5000,
    });
    this.activeAckSnackBar = ref;
    ref.onAction().subscribe(() => this.undoAcknowledgements());
    ref.afterDismissed().subscribe(() => {
      // Only the snackbar that is still current clears the stack — a snackbar replaced by a newer
      // one also fires afterDismissed, but its pending items were carried into the replacement.
      if (this.activeAckSnackBar === ref) {
        this.activeAckSnackBar = null;
        this.pendingUndo = [];
      }
    });
  }

  private undoAcknowledgements(): void {
    const toRestore = this.pendingUndo;
    this.pendingUndo = [];
    for (const notification of toRestore) {
      this.notificationsService.unacknowledge(notification).subscribe({
        error: () => {
          this.snackBar.open('Could not undo', undefined, { duration: 4000 });
        },
      });
    }
  }

  /**
   * The acknowledged row is optimistically removed, destroying the button that held keyboard focus —
   * without intervention focus falls to <body> behind the still-open menu. Once the list has
   * re-rendered, move focus to the next remaining action; when the last notification was handled,
   * close the menu instead (which returns focus to its trigger) rather than leaving an empty panel
   * trapping the user.
   */
  private restoreMenuFocus(panel: HTMLElement | null): void {
    if (!panel) {
      return;
    }
    setTimeout(() => {
      if (!panel.isConnected) {
        return; // The menu already closed.
      }
      const next =
        panel.querySelector<HTMLElement>('.notification-dismiss') ??
        panel.querySelector<HTMLElement>('.mat-mdc-menu-item');
      if (next) {
        next.focus();
        return;
      }
      this.menuTriggers?.filter(t => t.menuOpen).forEach(t => t.closeMenu());
    });
  }

  private static fallbackRoute(type: NotificationType): string | null {
    switch (type) {
      case 'Registration':
        return '/manage/users';
      case 'HeldMessage':
        return '/manage/approvals';
      case 'VendorFlag':
        return '/residents/vendors';
      default:
        return null;
    }
  }
}
