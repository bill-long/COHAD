import { Injectable } from '@angular/core';
import { Router, NavigationEnd } from '@angular/router';
import { Title } from '@angular/platform-browser';
import { ApplicationInsights } from '@microsoft/applicationinsights-web';
import { environment } from '../../environments/environment';
import { filter } from 'rxjs/operators';

@Injectable({ providedIn: 'root' })
export class ApplicationInsightsService {
  private appInsights: ApplicationInsights | null = null;
  private pendingUserContext: { userId: string; accountId?: string } | null = null;

  constructor(private router: Router, private titleService: Title) {}

  /** Call once from AppComponent.ngOnInit to start the SDK and router tracking. */
  init(): void {
    const connectionString = environment.appInsightsConnectionString;
    if (!connectionString) {
      return;
    }

    // Guard against multiple initializations: only set up the SDK and router tracking once.
    if (this.appInsights) {
      return;
    }

    this.appInsights = new ApplicationInsights({
      config: {
        connectionString,
        // Disable built-in SPA route tracking — we handle it manually via Angular Router events.
        enableAutoRouteTracking: false,
        disableFetchTracking: false,
        enableCorsCorrelation: true,
        // Do not collect HTTP headers to avoid leaking sensitive data (auth tokens, cookies, PII).
        enableRequestHeaderTracking: false,
        enableResponseHeaderTracking: false
      }
    });

    this.appInsights.loadAppInsights();

    // Apply any user context that was set before init() ran (e.g. from AuthService constructor).
    if (this.pendingUserContext) {
      this.appInsights.setAuthenticatedUserContext(this.pendingUserContext.userId, this.pendingUserContext.accountId);
      this.pendingUserContext = null;
    }

    // Track the initial page view (the router subscription only captures future navigations).
    this.appInsights.trackPageView({
      name: this.titleService.getTitle(),
      uri: this.router.url
    });

    this.router.events.pipe(
      filter((event): event is NavigationEnd => event instanceof NavigationEnd)
    ).subscribe(event => {
      this.appInsights!.trackPageView({
        name: this.titleService.getTitle(),
        uri: event.urlAfterRedirects
      });
    });
  }

  setAuthenticatedUser(userId: string, accountId?: string): void {
    if (this.appInsights) {
      this.appInsights.setAuthenticatedUserContext(userId, accountId);
    } else {
      this.pendingUserContext = { userId, accountId };
    }
  }

  clearAuthenticatedUser(): void {
    this.pendingUserContext = null;
    this.appInsights?.clearAuthenticatedUserContext();
  }

  trackEvent(name: string, properties?: Record<string, string>): void {
    this.appInsights?.trackEvent({ name }, properties);
  }

  trackException(error: Error, properties?: Record<string, string>): void {
    this.appInsights?.trackException({ exception: error }, properties);
  }

  flush(): void {
    this.appInsights?.flush();
  }
}
