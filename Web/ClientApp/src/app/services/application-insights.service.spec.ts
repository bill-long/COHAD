import { TestBed } from '@angular/core/testing';
import { Router, NavigationEnd } from '@angular/router';
import { Title } from '@angular/platform-browser';
import { Subject } from 'rxjs';
import { ApplicationInsightsService } from './application-insights.service';
import * as envModule from '../../environments/environment';

/** Jasmine spy object that stands in for the real ApplicationInsights SDK instance. */
type SdkSpy = jasmine.SpyObj<{
  loadAppInsights: () => void;
  trackPageView: (pageView: any) => void;
  trackEvent: (event: any, customProperties?: any) => void;
  trackException: (exception: any, customProperties?: any) => void;
  setAuthenticatedUserContext: (userId: string, accountId?: string) => void;
  clearAuthenticatedUserContext: () => void;
  flush: () => void;
}>;

function createSdkSpy(): SdkSpy {
  return jasmine.createSpyObj('ApplicationInsights', [
    'loadAppInsights', 'trackPageView', 'trackEvent', 'trackException',
    'setAuthenticatedUserContext', 'clearAuthenticatedUserContext', 'flush'
  ]);
}

describe('ApplicationInsightsService', () => {
  let service: ApplicationInsightsService;
  let routerEvents$: Subject<any>;
  let titleService: jasmine.SpyObj<Title>;
  let sdkMock: SdkSpy;

  function setup(connectionString: string): void {
    (envModule.environment as any).appInsightsConnectionString = connectionString;

    routerEvents$ = new Subject<any>();
    const routerSpy = jasmine.createSpyObj('Router', [], { events: routerEvents$.asObservable(), url: '/test' });
    titleService = jasmine.createSpyObj('Title', ['getTitle']);
    titleService.getTitle.and.returnValue('Test Page');

    sdkMock = createSdkSpy();

    TestBed.configureTestingModule({
      providers: [
        ApplicationInsightsService,
        { provide: Router, useValue: routerSpy },
        { provide: Title, useValue: titleService }
      ]
    });

    service = TestBed.inject(ApplicationInsightsService);

    // Replace the factory so the real SDK is never loaded.
    spyOn(service as any, 'createSdkInstance').and.returnValue(sdkMock);
  }

  afterEach(() => {
    (envModule.environment as any).appInsightsConnectionString = '';
  });

  describe('when connection string is empty', () => {
    beforeEach(() => setup(''));

    it('should not initialize Application Insights', () => {
      service.init();
      expect((service as any).createSdkInstance).not.toHaveBeenCalled();
      // These should be no-ops and not throw
      service.trackEvent('TestEvent');
      service.trackException(new Error('test'));
      service.setAuthenticatedUser('user-1');
      service.clearAuthenticatedUser();
      service.flush();
    });

    it('should not track page views on NavigationEnd', () => {
      service.init();
      routerEvents$.next(new NavigationEnd(1, '/test', '/test'));
      expect(sdkMock.trackPageView).not.toHaveBeenCalled();
    });
  });

  describe('when connection string is provided', () => {
    const testConnectionString = 'InstrumentationKey=test-key-00000000-0000-0000-0000-000000000000';

    beforeEach(() => setup(testConnectionString));

    it('should initialize the SDK', () => {
      service.init();
      expect((service as any).createSdkInstance).toHaveBeenCalledWith(testConnectionString);
    });

    it('should track initial page view on init', () => {
      service.init();
      expect(sdkMock.trackPageView).toHaveBeenCalledWith({
        name: 'Test Page',
        uri: '/test'
      });
    });

    it('should track page views on NavigationEnd', () => {
      service.init();
      sdkMock.trackPageView.calls.reset();
      routerEvents$.next(new NavigationEnd(1, '/residents/directory', '/residents/directory'));
      expect(sdkMock.trackPageView).toHaveBeenCalledWith({
        name: 'Test Page',
        uri: '/residents/directory'
      });
    });

    it('should strip query strings from tracked URIs', () => {
      service.init();
      sdkMock.trackPageView.calls.reset();
      routerEvents$.next(new NavigationEnd(2, '/callback?code=secret&state=abc', '/callback?code=secret&state=abc'));
      expect(sdkMock.trackPageView).toHaveBeenCalledWith({
        name: 'Test Page',
        uri: '/callback'
      });
    });

    it('should strip fragments from tracked URIs', () => {
      service.init();
      sdkMock.trackPageView.calls.reset();
      routerEvents$.next(new NavigationEnd(3, '/page#section', '/page#section'));
      expect(sdkMock.trackPageView).toHaveBeenCalledWith({
        name: 'Test Page',
        uri: '/page'
      });
    });

    it('should delegate trackEvent to the SDK', () => {
      service.init();
      service.trackEvent('TestEvent', { key: 'value' });
      expect(sdkMock.trackEvent).toHaveBeenCalledWith({ name: 'TestEvent' }, { key: 'value' });
    });

    it('should delegate trackException to the SDK', () => {
      service.init();
      const error = new Error('test error');
      service.trackException(error);
      expect(sdkMock.trackException).toHaveBeenCalledWith({ exception: error }, undefined);
    });

    it('should delegate setAuthenticatedUser to the SDK', () => {
      service.init();
      service.setAuthenticatedUser('user-1');
      expect(sdkMock.setAuthenticatedUserContext).toHaveBeenCalledWith('user-1', undefined);
    });

    it('should delegate clearAuthenticatedUser to the SDK', () => {
      service.init();
      service.clearAuthenticatedUser();
      expect(sdkMock.clearAuthenticatedUserContext).toHaveBeenCalled();
    });

    it('should apply buffered user context when init() runs after setAuthenticatedUser', () => {
      service.setAuthenticatedUser('user-1');
      service.init();
      expect(sdkMock.setAuthenticatedUserContext).toHaveBeenCalledWith('user-1', undefined);
      expect((service as any).pendingUserContext).toBeNull();
    });

    it('should not apply buffered user context if clearAuthenticatedUser was called', () => {
      service.setAuthenticatedUser('user-1');
      service.clearAuthenticatedUser();
      service.init();
      expect(sdkMock.setAuthenticatedUserContext).not.toHaveBeenCalled();
      expect((service as any).pendingUserContext).toBeNull();
    });
  });
});
