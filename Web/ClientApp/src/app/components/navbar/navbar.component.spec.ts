import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { NoopAnimationsModule } from '@angular/platform-browser/animations';
import { RouterModule, provideRouter } from '@angular/router';
import { MatToolbarModule } from '@angular/material/toolbar';
import { MatMenuModule, MatMenuTrigger } from '@angular/material/menu';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatBadgeModule } from '@angular/material/badge';
import { BehaviorSubject, Subject, of } from 'rxjs';

import { NavbarComponent } from './navbar.component';
import { ApplicationState, applicationState, dispatcher, initialStateValue } from 'src/app/state';
import { EventsService } from 'src/app/services/events.service';
import { NotificationsService } from 'src/app/services/notifications.service';
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
      declarations: [NavbarComponent],
      imports: [
        NoopAnimationsModule,
        RouterModule,
        MatToolbarModule,
        MatMenuModule,
        MatIconModule,
        MatButtonModule,
        MatBadgeModule,
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

  it('does not capture any nested sub-menu content as the hamburger menu own lazy content', () => {
    const trigger = hamburgerTrigger();

    // `lazyContent` is only populated when a matMenuContent template is projected directly into THIS
    // menu. The hamburger menu has none of its own, so capturing one signals the nesting regression.
    // Cast: lazyContent is a MatMenu implementation detail, not on the public MatMenuPanel interface.
    const menu = trigger.menu as { lazyContent?: unknown } | null;
    expect(menu?.lazyContent).toBeFalsy();
  });
});
