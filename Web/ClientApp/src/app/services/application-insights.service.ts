import { Injectable } from '@angular/core';
import { Router, NavigationEnd } from '@angular/router';
import { Title } from '@angular/platform-browser';
import { ApplicationInsights } from '@microsoft/applicationinsights-web';
import { environment } from 'src/environments/environment';
import { filter } from 'rxjs/operators';

@Injectable({ providedIn: 'root' })
export class ApplicationInsightsService {
  private appInsights: ApplicationInsights | null = null;
  private pendingUserContext: { userId: string; accountId?: string } | null = null;

  constructor(
    private router: Router,
    private titleService: Title,
  ) {}

  /** Override in tests to return a mock SDK instance. */
  protected createSdkInstance(connectionString: string): ApplicationInsights {
    const ai = new ApplicationInsights({
      config: {
        connectionString,
        // Disable built-in SPA route tracking — we handle it manually via Angular Router events.
        enableAutoRouteTracking: false,
        disableFetchTracking: false,
        enableCorsCorrelation: true,
        correlationHeaderExcludedDomains: ['*.b2clogin.com'],
        // Do not collect HTTP headers to avoid leaking sensitive data (auth tokens, cookies, PII).
        enableRequestHeaderTracking: false,
        enableResponseHeaderTracking: false,
      },
    });
    ai.loadAppInsights();
    return ai;
  }

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

    this.appInsights = this.createSdkInstance(connectionString);

    // Apply any user context that was set before init() ran (e.g. from AuthService constructor).
    if (this.pendingUserContext) {
      this.appInsights.setAuthenticatedUserContext(this.pendingUserContext.userId, this.pendingUserContext.accountId);
      this.pendingUserContext = null;
    }

    // Track the initial page view (the router subscription only captures future navigations).
    this.appInsights.trackPageView({
      name: this.titleService.getTitle(),
      uri: this.stripQueryAndFragment(this.router.url),
    });

    this.router.events.pipe(filter((event): event is NavigationEnd => event instanceof NavigationEnd)).subscribe(event => {
      this.appInsights!.trackPageView({
        name: this.titleService.getTitle(),
        uri: this.stripQueryAndFragment(event.urlAfterRedirects),
      });
    });
  }

  /** Remove query string and fragment to avoid leaking sensitive params (e.g. OAuth code/state). */
  private stripQueryAndFragment(url: string): string {
    const qIndex = url.indexOf('?');
    const hIndex = url.indexOf('#');
    if (qIndex === -1 && hIndex === -1) {
      return url;
    }
    const end = qIndex !== -1 && hIndex !== -1 ? Math.min(qIndex, hIndex) : qIndex !== -1 ? qIndex : hIndex;
    return url.substring(0, end);
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
