import { ComponentFixture, TestBed, fakeAsync, flush, tick } from '@angular/core/testing';
import { A11yModule } from '@angular/cdk/a11y';
import { By } from '@angular/platform-browser';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { RouterModule, provideRouter } from '@angular/router';
import { MatToolbarModule } from '@angular/material/toolbar';
import { MatMenuModule, MatMenuTrigger } from '@angular/material/menu';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatBadgeModule } from '@angular/material/badge';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatSnackBarModule } from '@angular/material/snack-bar';
import { BehaviorSubject, Subject, of } from 'rxjs';

import { NavbarComponent } from './navbar.component';
import { TimeAgoPipe } from 'src/app/pipes/time-ago.pipe';
import { ApplicationState, applicationState, dispatcher, initialStateValue } from 'src/app/state';
import { EventsService } from 'src/app/services/events.service';
import { AppNotification, NotificationsService } from 'src/app/services/notifications.service';
import { ThemeService } from 'src/app/services/theme.service';

/**
 * Regression coverage for the mobile hamburger menu. The bug: sub-menus (e.g. #notificationMenuMobile,
 * which carries an <ng-template matMenuContent>) were declared physically nested inside the
 * #hamburgerMenu <mat-menu>. MatMenu's `lazyContent` content query uses `descendants: true`, so the
 * hamburger menu captured the nested menu's content as its own and threw
 * "Cannot read properties of null (reading 'insertBefore')" on open - breaking the menu for every
 * visitor, guests included (the nested <mat-menu> declaration is always in the DOM even when its
 * trigger button is *ngIf-hidden). These tests use the real MatMenuModule (not NO_ERRORS_SCHEMA) so
 * the content query and open path actually execute.
 */
describe('NavbarComponent (mobile hamburger menu)', () => {
  let fixture: ComponentFixture<NavbarComponent>;
  let appState$: BehaviorSubject<ApplicationState>;

  beforeEach(async () => {
    appState$ = new BehaviorSubject<ApplicationState>(initialStateValue);

    await TestBed.configureTestingModule({
      declarations: [NavbarComponent, TimeAgoPipe],
      imports: [
        NoopAnimationsModule,
        RouterModule,
        MatToolbarModule,
        MatMenuModule,
        MatIconModule,
        MatButtonModule,
        MatBadgeModule,
        MatTooltipModule,
        MatSnackBarModule,
        A11yModule,
      ],
      providers: [
        provideRouter([]),
        { provide: applicationState, useValue: appState$.asObservable() },
        { provide: dispatcher, useValue: new Subject() },
        { provide: EventsService, useValue: { getUpcoming: () => of([]) } },
        { provide: NotificationsService, useValue: { notifications$: of([]), unreadCount$: of(0) } },
        { provide: ThemeService, useValue: { isDarkTheme$: of(false), toggleTheme: () => {} } },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(NavbarComponent);
    fixture.detectChanges();
  });

  afterEach(() => {
    // Destroy so the CDK overlay panel opened during a test is disposed and does not leak into the next.
    fixture.destroy();
  });

  function hamburgerTrigger(): MatMenuTrigger {
    const btn = fixture.debugElement.query(By.css('button[aria-label="Open navigation menu"]'));
    expect(btn).withContext('hamburger trigger button should render').not.toBeNull();
    return btn.injector.get(MatMenuTrigger);
  }

  it('opens without throwing and shows the navigation items', () => {
    const trigger = hamburgerTrigger();

    expect(() => trigger.openMenu()).not.toThrow();
    fixture.detectChanges();

    expect(trigger.menuOpen).toBeTrue();
    const panel = document.querySelector('.mat-mdc-menu-panel');
    expect(panel).withContext('menu panel should be attached to the overlay').not.toBeNull();
    expect(panel!.textContent).toContain('Home');
  });

  it("does not capture any nested sub-menu content as the hamburger menu's own lazy content", () => {
    const trigger = hamburgerTrigger();

    // `lazyContent` is only populated when a matMenuContent template is projected directly into THIS
    // menu. The hamburger menu has none of its own, so capturing one signals the nesting regression.
    // Cast: lazyContent is a MatMenu implementation detail, not on the public MatMenuPanel interface.
    const menu = trigger.menu as { lazyContent?: unknown } | null;
    expect(menu?.lazyContent).toBeFalsy();
  });
});

/**
 * The notification bell menu renders each notification as a single row: the clickable open area plus,
 * for acknowledgeable types (registrations), an inline mark-as-handled icon button on the trailing
 * edge. Regression: the acknowledge action used to be a second full-width "Acknowledge" menu item
 * under every registration, which stacked into a wall of identical lines with no visible connection
 * to the notification each one resolved.
 */
describe('NavbarComponent (notification bell menu)', () => {
  let fixture: ComponentFixture<NavbarComponent>;
  let notifications$: BehaviorSubject<AppNotification[]>;
  let notificationsService: jasmine.SpyObj<NotificationsService> & {
    notifications$: unknown;
    unreadCount$: unknown;
  };

  function makeNotification(overrides: Partial<AppNotification>): AppNotification {
    return {
      id: 'n-1',
      type: 'Registration',
      targetType: 'User',
      targetId: 'u-1',
      title: 'New user registered',
      summary: 'pat@example.com',
      deepLink: null,
      // Relative to now so timeAgo assertions don't rot as the calendar advances.
      createdUtc: new Date(Date.now() - 5 * 60_000).toISOString(),
      ...overrides,
    };
  }

  const notifications: AppNotification[] = [
    makeNotification({ id: 'n-1', title: 'New user registered', summary: 'pat@example.com' }),
    makeNotification({
      id: 'n-2',
      title: 'Another user registered',
      summary: 'sam@example.com',
      createdUtc: new Date(Date.now() - 2 * 3_600_000).toISOString(),
    }),
    makeNotification({
      id: 'n-3',
      type: 'HeldMessage',
      targetType: 'HeldMessage',
      targetId: 'h-1',
      title: 'Held message',
      summary: 'From outside the directory',
    }),
  ];

  beforeEach(async () => {
    const spy = jasmine.createSpyObj<NotificationsService>('NotificationsService', ['acknowledge', 'unacknowledge']);
    spy.acknowledge.and.returnValue(of(void 0));
    spy.unacknowledge.and.returnValue(of(void 0));
    notifications$ = new BehaviorSubject<AppNotification[]>(notifications);
    notificationsService = Object.assign(spy, {
      notifications$: notifications$.asObservable(),
      unreadCount$: of(notifications.length),
    });

    // Bell visibility requires completed auth bootstrap + an apiUser holding a committee-manage role.
    const adminState: ApplicationState = {
      ...initialStateValue,
      authBootstrapStatus: 'completed',
      authSessionResolved: true,
      apiUser: {
        uniqueId: 'admin-1',
        createdTime: '',
        modifiedTime: '',
        creatorId: '',
        modifierId: '',
        givenName: 'Admin',
        surname: 'User',
        displayName: 'Admin User',
        identityProvider: 'idp',
        email: 'admin@example.com',
        streetAddress: '',
        roles: ['Administrator', 'Resident'],
        ownedHomes: [],
      },
    };

    await TestBed.configureTestingModule({
      declarations: [NavbarComponent, TimeAgoPipe],
      imports: [
        NoopAnimationsModule,
        RouterModule,
        MatToolbarModule,
        MatMenuModule,
        MatIconModule,
        MatButtonModule,
        MatBadgeModule,
        MatTooltipModule,
        MatSnackBarModule,
        A11yModule,
      ],
      providers: [
        provideRouter([]),
        { provide: applicationState, useValue: new BehaviorSubject<ApplicationState>(adminState).asObservable() },
        { provide: dispatcher, useValue: new Subject() },
        { provide: EventsService, useValue: { getUpcoming: () => of([]) } },
        { provide: NotificationsService, useValue: notificationsService },
        { provide: ThemeService, useValue: { isDarkTheme$: of(false), toggleTheme: () => {} } },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(NavbarComponent);
    fixture.detectChanges();
  });

  afterEach(() => {
    fixture.destroy();
  });

  function openBellMenu(): MatMenuTrigger {
    const btn = fixture.debugElement.query(By.css('button[aria-label="Notifications"]'));
    expect(btn).withContext('notification bell button should render for an Administrator').not.toBeNull();
    const trigger = btn.injector.get(MatMenuTrigger);
    trigger.openMenu();
    fixture.detectChanges();
    return trigger;
  }

  it('renders one row per notification with no separate Acknowledge menu item', () => {
    openBellMenu();

    const panel = document.querySelector('.mat-mdc-menu-panel')!;
    expect(panel.querySelectorAll('.notification-item').length).toBe(3);
    expect(panel.querySelectorAll('.mat-mdc-menu-item').length)
      .withContext('the open action should be the only menu item per notification')
      .toBe(3);
    expect(panel.textContent).not.toContain('Acknowledge');
  });

  it('shows the inline mark-as-handled button only for Registration notifications', () => {
    openBellMenu();

    const rows = document.querySelectorAll('.mat-mdc-menu-panel .notification-item');
    expect(rows[0].querySelector('.notification-dismiss')).not.toBeNull();
    expect(rows[1].querySelector('.notification-dismiss')).not.toBeNull();
    expect(rows[2].querySelector('.notification-dismiss'))
      .withContext('held messages resolve via their moderation page, not acknowledge')
      .toBeNull();
  });

  it('shows a relative timestamp on each notification row', () => {
    openBellMenu();

    const times = Array.from(
      document.querySelectorAll('.mat-mdc-menu-panel .notification-time'),
      el => el.textContent!.trim(),
    );
    expect(times.slice(0, 2)).toEqual(['5m ago', '2h ago']);
  });

  it('offers undo via snackbar after marking a notification handled', () => {
    openBellMenu();

    document.querySelector<HTMLButtonElement>('.mat-mdc-menu-panel .notification-dismiss')!.click();
    fixture.detectChanges();

    const snackBar = document.querySelector('.mat-mdc-snack-bar-container');
    expect(snackBar).withContext('a snackbar should confirm the action').not.toBeNull();
    expect(snackBar!.textContent).toContain('Marked as handled');

    const undo = snackBar!.querySelector<HTMLButtonElement>('.mat-mdc-snack-bar-action');
    expect(undo).withContext('the snackbar should carry an Undo action').not.toBeNull();
    undo!.click();
    fixture.detectChanges();

    expect(notificationsService.unacknowledge).toHaveBeenCalledTimes(1);
    expect(notificationsService.unacknowledge.calls.mostRecent().args[0].id).toBe('n-1');
  });

  it('accumulates rapid acknowledgements into one snackbar whose Undo restores all of them', fakeAsync(() => {
    openBellMenu();
    const dismiss = document.querySelectorAll<HTMLButtonElement>('.mat-mdc-menu-panel .notification-dismiss');
    dismiss[0].click();
    fixture.detectChanges();
    dismiss[1].click();
    fixture.detectChanges();
    tick(); // settle the replaced snackbar's detach without firing the 5s duration timer

    // The replaced first snackbar may still be detaching; the live one is the last container.
    const containers = document.querySelectorAll('.mat-mdc-snack-bar-container');
    const snackBar = containers[containers.length - 1];
    expect(snackBar.textContent).toContain('2 marked as handled');

    snackBar.querySelector<HTMLButtonElement>('.mat-mdc-snack-bar-action')!.click();
    fixture.detectChanges();
    tick();

    expect(notificationsService.unacknowledge).toHaveBeenCalledTimes(2);
    const ids = notificationsService.unacknowledge.calls.allArgs().map(args => args[0].id);
    expect(ids).toEqual(['n-1', 'n-2']);
  }));

  /** Three Registrations so below/above/nearest preferences are distinguishable from "first in list". */
  function threeRegistrations(): AppNotification[] {
    return [
      makeNotification({ id: 'r-1', title: 'First' }),
      makeNotification({ id: 'r-2', title: 'Second' }),
      makeNotification({ id: 'r-3', title: 'Third' }),
    ];
  }

  function dismissButtonFor(id: string): HTMLButtonElement {
    return document.querySelector<HTMLButtonElement>(
      `.mat-mdc-menu-panel .notification-item[data-notification-id="${id}"] .notification-dismiss`,
    )!;
  }

  function focusedLabel(): string | null | undefined {
    return (document.activeElement as HTMLElement | null)?.getAttribute('aria-label');
  }

  it('moves focus to the neighbor below the handled row, not the top of the list', fakeAsync(() => {
    notifications$.next(threeRegistrations());
    openBellMenu();
    const middle = dismissButtonFor('r-2');
    middle.focus();
    middle.click();
    // The service's optimistic removal re-renders the list without the handled row.
    notifications$.next(threeRegistrations().filter(n => n.id !== 'r-2'));
    fixture.detectChanges();
    flush();

    expect(focusedLabel())
      .withContext('focus should land on the row below the handled one, not jump to the first row')
      .toBe('Mark as handled: Third');
  }));

  it('moves focus to the neighbor above when the last row is handled', fakeAsync(() => {
    notifications$.next(threeRegistrations());
    openBellMenu();
    const last = dismissButtonFor('r-3');
    last.focus();
    last.click();
    notifications$.next(threeRegistrations().filter(n => n.id !== 'r-3'));
    fixture.detectChanges();
    flush();

    expect(focusedLabel())
      .withContext('handling the bottom row should move focus to the row above it')
      .toBe('Mark as handled: Second');
  }));

  it('focus preference is by identity, so a notification arriving mid-flight does not steal it', fakeAsync(() => {
    notifications$.next(threeRegistrations());
    openBellMenu();
    const middle = dismissButtonFor('r-2');
    middle.focus();
    middle.click();
    // A new registration lands at the top (newest-first sort) before the removal re-renders.
    const arrived = makeNotification({ id: 'r-0', title: 'Brand new' });
    notifications$.next([arrived, ...threeRegistrations().filter(n => n.id !== 'r-2')]);
    fixture.detectChanges();
    flush();

    expect(focusedLabel())
      .withContext('focus should follow the surviving neighbor, not an unread arrival occupying its old index')
      .toBe('Mark as handled: Third');
  }));

  it('falls back to a non-actionable neighboring row rather than warping to the top', fakeAsync(() => {
    // [Registration, Registration, HeldMessage]: handling the middle Registration leaves the held
    // message as the row taking its place; its open button should receive focus even though it has
    // no mark-as-handled action.
    openBellMenu();
    const middle = dismissButtonFor('n-2');
    middle.focus();
    middle.click();
    notifications$.next(notifications.filter(n => n.id !== 'n-2'));
    fixture.detectChanges();
    flush();

    const active = document.activeElement as HTMLElement | null;
    expect(active?.closest('.notification-item')?.getAttribute('data-notification-id'))
      .withContext('the held-message row below should take focus, not the first row')
      .toBe('n-3');
  }));

  it('rapid successive acknowledgements land focus on the nearest surviving neighbor', fakeAsync(() => {
    notifications$.next(threeRegistrations());
    openBellMenu();
    dismissButtonFor('r-1').click();
    dismissButtonFor('r-2').click();
    notifications$.next(threeRegistrations().filter(n => n.id === 'r-3'));
    fixture.detectChanges();
    flush();

    expect(focusedLabel())
      .withContext('both restores should skip the concurrently-removed rows and settle on the survivor')
      .toBe('Mark as handled: Third');
  }));

  it('closes the menu when the last notification is handled', fakeAsync(() => {
    notifications$.next([makeNotification({ id: 'only' })]);
    const trigger = openBellMenu();

    document.querySelector<HTMLButtonElement>('.mat-mdc-menu-panel .notification-dismiss')!.click();
    notifications$.next([]);
    fixture.detectChanges();
    flush();

    expect(trigger.menuOpen)
      .withContext('an emptied menu should close and hand focus back to the bell')
      .toBeFalse();
  }));

  it('labels each mark-as-handled button with its notification title', () => {
    openBellMenu();

    const dismiss = document.querySelector('.mat-mdc-menu-panel .notification-dismiss')!;
    expect(dismiss.getAttribute('aria-label')).toBe('Mark as handled: New user registered');
  });

  it('keeps the menu open on Tab so the mark-as-handled button is keyboard-reachable', () => {
    const trigger = openBellMenu();

    // MatMenu normally closes itself on Tab (keyManager.tabOut). The rows contain the keystroke so
    // keyboard users can move from a notification into its inline mark-as-handled button.
    const openButton = document.querySelector<HTMLButtonElement>(
      '.mat-mdc-menu-panel .notification-item .mat-mdc-menu-item',
    )!;
    openButton.dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', bubbles: true, cancelable: true }));
    fixture.detectChanges();

    expect(trigger.menuOpen)
      .withContext('Tab within a notification row must not close the menu')
      .toBeTrue();
  });

  it('acknowledges the clicked notification and keeps the menu open', () => {
    const trigger = openBellMenu();

    const dismiss = document.querySelectorAll<HTMLButtonElement>('.mat-mdc-menu-panel .notification-dismiss')[1];
    dismiss.click();
    fixture.detectChanges();

    expect(notificationsService.acknowledge).toHaveBeenCalledOnceWith('n-2');
    expect(trigger.menuOpen)
      .withContext('handling one notification should not close the menu over the others')
      .toBeTrue();
  });
});
