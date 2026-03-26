import { Injectable } from '@angular/core';
import { Router, NavigationEnd } from '@angular/router';
import { Title } from '@angular/platform-browser';
import { ApplicationInsights } from '@microsoft/applicationinsights-web';
import { environment } from '../../environments/environment';
import { filter } from 'rxjs/operators';

@Injectable({ providedIn: 'root' })
export class ApplicationInsightsService {
  private appInsights: ApplicationInsights | null = null;

  constructor(private router: Router, private titleService: Title) {}

  /** Call once from AppComponent.ngOnInit to start the SDK and router tracking. */
  init(): void {
    const connectionString = environment.appInsightsConnectionString;
    if (!connectionString) {
      return;
    }

    this.appInsights = new ApplicationInsights({
      config: {
        connectionString,
        // Disable built-in SPA route tracking — we handle it manually via Angular Router events.
        enableAutoRouteTracking: false,
        disableFetchTracking: false,
        enableCorsCorrelation: true,
        enableRequestHeaderTracking: true,
        enableResponseHeaderTracking: true
      }
    });

    this.appInsights.loadAppInsights();

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
    this.appInsights?.setAuthenticatedUserContext(userId, accountId, true);
  }

  clearAuthenticatedUser(): void {
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
