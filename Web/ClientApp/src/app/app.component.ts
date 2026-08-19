import { Component, ElementRef, OnInit, ViewChild } from '@angular/core';
import { Inject } from '@angular/core';
import { Observable } from 'rxjs';
import { filter, map, shareReplay, skip } from 'rxjs/operators';
import { NavigationEnd, Router } from '@angular/router';
import { AuthService } from './services/auth.service';
import { MeService } from './services/me.service';
import { DirectoryService } from './services/directory.service';
import { UserService } from './services/user.service';
import { HomeService } from './services/home.service';
import { ThemeService } from './services/theme.service';
import { ApplicationInsightsService } from './services/application-insights.service';
import { applicationState, ApplicationState } from './state';

@Component({
  selector: 'app-root',
  templateUrl: './app.component.html',
  styleUrls: ['./app.component.css'],
  standalone: false,
})
export class AppComponent implements OnInit {
  @ViewChild('mainContent') private mainContent?: ElementRef<HTMLElement>;

  /**
   * Shared so the template can both render the overlay and mark the rest of the
   * page inert from a single subscription.
   */
  readonly showPostLoginTransition$: Observable<boolean>;

  constructor(
    private authService: AuthService,
    private meService: MeService,
    private dirService: DirectoryService,
    private userService: UserService,
    private homeService: HomeService,
    private themeService: ThemeService,
    private telemetry: ApplicationInsightsService,
    private router: Router,
    @Inject(applicationState) private appState: Observable<ApplicationState>,
  ) {
    this.showPostLoginTransition$ = this.appState.pipe(
      map(s => s.authBootstrapStatus === 'inProgress'),
      shareReplay({ bufferSize: 1, refCount: true }),
    );
  }

  ngOnInit(): void {
    this.themeService.initializeTheme();
    this.telemetry.init();

    // A SPA route change swaps the page under the user without moving focus, so
    // keyboard and screen-reader users are left where they were - typically on
    // the nav link they just activated. Move focus into <main> so the next Tab
    // continues from the new page's content. skip(1) leaves the initial page
    // load alone: nothing was navigated. The spoken announcement is raised by
    // CohadTitleStrategy, which owns the title text.
    // AppComponent lives for the whole app lifetime, so this needs no teardown
    // (same convention as the router subscription in NavbarComponent).
    this.router.events
      .pipe(
        filter((e): e is NavigationEnd => e instanceof NavigationEnd),
        skip(1),
      )
      .subscribe(() => {
        // preventScroll so this does not fight the router's own
        // scrollPositionRestoration when the user navigates back.
        this.mainContent?.nativeElement.focus({ preventScroll: true });
      });
  }
}
