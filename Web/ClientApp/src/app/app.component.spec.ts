import { TestBed, waitForAsync } from '@angular/core/testing';
import { AppComponent } from './app.component';
import { CUSTOM_ELEMENTS_SCHEMA } from '@angular/core';
import { AuthService } from './services/auth.service';
import { MeService } from './services/me.service';
import { DirectoryService } from './services/directory.service';
import { UserService } from './services/user.service';
import { HomeService } from './services/home.service';
import { ThemeService } from './services/theme.service';
import { ApplicationInsightsService } from './services/application-insights.service';
import { BehaviorSubject, Subject } from 'rxjs';
import { NavigationEnd, Router } from '@angular/router';
import { applicationState, initialStateValue } from './state';

describe('AppComponent', () => {
  let routerEvents: Subject<NavigationEnd>;

  beforeEach(waitForAsync(() => {
    routerEvents = new Subject<NavigationEnd>();
    TestBed.configureTestingModule({
      declarations: [AppComponent],
      providers: [
        { provide: AuthService, useValue: {} },
        { provide: MeService, useValue: {} },
        { provide: DirectoryService, useValue: {} },
        { provide: UserService, useValue: {} },
        { provide: HomeService, useValue: {} },
        { provide: ThemeService, useValue: { initializeTheme: () => undefined } },
        { provide: ApplicationInsightsService, useValue: { init: () => undefined } },
        { provide: applicationState, useValue: new BehaviorSubject(initialStateValue) },
        { provide: Router, useValue: { events: routerEvents } },
      ],
      schemas: [CUSTOM_ELEMENTS_SCHEMA],
    }).compileComponents();
  }));

  it('should create the app', () => {
    const fixture = TestBed.createComponent(AppComponent);
    const app = fixture.debugElement.componentInstance;
    expect(app).toBeTruthy();
  });

  it('leaves the initial navigation alone', () => {
    const fixture = TestBed.createComponent(AppComponent);
    fixture.detectChanges();

    routerEvents.next(new NavigationEnd(1, '/', '/'));

    expect(document.activeElement).not.toBe(mainOf(fixture));
  });

  it('moves focus to <main> on a route change', () => {
    const fixture = TestBed.createComponent(AppComponent);
    fixture.detectChanges();

    // The first event is the initial page load and is deliberately skipped.
    routerEvents.next(new NavigationEnd(1, '/', '/'));
    routerEvents.next(new NavigationEnd(2, '/residents/directory', '/residents/directory'));

    expect(document.activeElement).toBe(mainOf(fixture));
  });

  function mainOf(fixture: { nativeElement: HTMLElement }): HTMLElement | null {
    return fixture.nativeElement.querySelector('main#main-content');
  }
});
