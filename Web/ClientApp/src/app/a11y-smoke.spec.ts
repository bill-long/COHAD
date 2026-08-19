import { TestBed, waitForAsync } from '@angular/core/testing';
import { CUSTOM_ELEMENTS_SCHEMA } from '@angular/core';
import { BehaviorSubject, Subject } from 'rxjs';
import { NavigationEnd, Router } from '@angular/router';
import { LiveAnnouncer } from '@angular/cdk/a11y';
import { Title } from '@angular/platform-browser';
import axe from 'axe-core';
import { AppComponent } from './app.component';
import { AuthService } from './services/auth.service';
import { MeService } from './services/me.service';
import { DirectoryService } from './services/directory.service';
import { UserService } from './services/user.service';
import { HomeService } from './services/home.service';
import { ThemeService } from './services/theme.service';
import { ApplicationInsightsService } from './services/application-insights.service';
import { applicationState, initialStateValue } from './state';

/**
 * Structural accessibility checks on the app shell.
 *
 * Scope note: this mounts AppComponent alone, so it covers the shell template
 * and not the components it hosts. See the axe test below.
 *
 * These lock the landmarks and the skip link, which every route depends on and
 * which nothing else tests - the ESLint template rules see single elements, not
 * document structure.
 */
describe('app shell accessibility', () => {
  beforeEach(waitForAsync(() => {
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
        { provide: Router, useValue: { events: new Subject<NavigationEnd>() } },
        { provide: Title, useValue: { getTitle: () => 'COHAD' } },
        { provide: LiveAnnouncer, useValue: { announce: () => Promise.resolve() } },
      ],
      schemas: [CUSTOM_ELEMENTS_SCHEMA],
    }).compileComponents();
  }));

  it('exposes a main landmark that the skip link targets', () => {
    const fixture = TestBed.createComponent(AppComponent);
    fixture.detectChanges();
    const host: HTMLElement = fixture.nativeElement;

    const skipLink = host.querySelector<HTMLAnchorElement>('a.skip-link');
    const main = host.querySelector('main');

    expect(skipLink).withContext('skip link is missing').not.toBeNull();
    expect(main).withContext('main landmark is missing').not.toBeNull();
    // The href and the id have to agree or the skip link silently does nothing.
    expect(skipLink!.getAttribute('href')).toBe(`#${main!.id}`);
  });

  it('makes the main landmark programmatically focusable for route changes', () => {
    const fixture = TestBed.createComponent(AppComponent);
    fixture.detectChanges();
    const main = fixture.nativeElement.querySelector('main') as HTMLElement;

    expect(main.getAttribute('tabindex')).toBe('-1');
  });

  it('reports no axe violations for the shell', async () => {
    const fixture = TestBed.createComponent(AppComponent);
    fixture.detectChanges();

    const results = await axe.run(fixture.nativeElement as HTMLElement, {
      // Colour contrast is locked separately, against the design tokens, in
      // a11y-contrast.spec.ts - axe cannot judge it inside a detached fixture.
      rules: { 'color-contrast': { enabled: false } },
    });

    const summary = results.violations.map(v => `${v.id}: ${v.help}`).join('\n');
    expect(results.violations.length).withContext(summary).toBe(0);
  });
});
