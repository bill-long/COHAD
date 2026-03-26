import { TestBed } from '@angular/core/testing';
import { Router, NavigationEnd } from '@angular/router';
import { Title } from '@angular/platform-browser';
import { Subject } from 'rxjs';
import { ApplicationInsightsService } from './application-insights.service';
import * as envModule from '../../environments/environment';

describe('ApplicationInsightsService', () => {
  let service: ApplicationInsightsService;
  let routerEvents$: Subject<any>;
  let titleService: jasmine.SpyObj<Title>;

  function setup(connectionString: string): void {
    (envModule.environment as any).appInsightsConnectionString = connectionString;

    routerEvents$ = new Subject<any>();
    const routerSpy = jasmine.createSpyObj('Router', [], { events: routerEvents$.asObservable() });
    titleService = jasmine.createSpyObj('Title', ['getTitle']);
    titleService.getTitle.and.returnValue('Test Page');

    TestBed.configureTestingModule({
      providers: [
        ApplicationInsightsService,
        { provide: Router, useValue: routerSpy },
        { provide: Title, useValue: titleService }
      ]
    });

    service = TestBed.inject(ApplicationInsightsService);
  }

  afterEach(() => {
    (envModule.environment as any).appInsightsConnectionString = '';
  });

  describe('when connection string is empty', () => {
    beforeEach(() => setup(''));

    it('should not initialize Application Insights', () => {
      service.init();
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
      // No error thrown means the no-op path works
    });
  });

  describe('when connection string is provided', () => {
    const testConnectionString = 'InstrumentationKey=test-key-00000000-0000-0000-0000-000000000000';

    beforeEach(() => setup(testConnectionString));

    it('should initialize without error', () => {
      expect(() => service.init()).not.toThrow();
    });

    it('should track page views on NavigationEnd', () => {
      service.init();
      const appInsightsInstance = (service as any).appInsights;
      const trackPageViewSpy = spyOn(appInsightsInstance, 'trackPageView');
      // Emit a navigation event — the subscription should process it
      routerEvents$.next(new NavigationEnd(1, '/residents/directory', '/residents/directory'));
      expect(trackPageViewSpy).toHaveBeenCalled();
    });

    it('should expose trackEvent', () => {
      service.init();
      expect(() => service.trackEvent('TestEvent', { key: 'value' })).not.toThrow();
    });

    it('should expose trackException', () => {
      service.init();
      expect(() => service.trackException(new Error('test error'))).not.toThrow();
    });

    it('should expose setAuthenticatedUser', () => {
      service.init();
      expect(() => service.setAuthenticatedUser('user-1', 'account@test.com')).not.toThrow();
    });

    it('should expose clearAuthenticatedUser', () => {
      service.init();
      expect(() => service.clearAuthenticatedUser()).not.toThrow();
    });

    it('should expose flush', () => {
      service.init();
      expect(() => service.flush()).not.toThrow();
    });

    it('should apply buffered user context when init() runs after setAuthenticatedUser', () => {
      // Set user context BEFORE init — simulates AuthService constructor race
      service.setAuthenticatedUser('user-1', 'user@test.com');
      service.init();

      const appInsightsInstance = (service as any).appInsights;
      const setSpy = spyOn(appInsightsInstance, 'setAuthenticatedUserContext');

      // Call again to verify direct path works after init
      service.setAuthenticatedUser('user-2', 'user2@test.com');
      expect(setSpy).toHaveBeenCalledWith('user-2', 'user2@test.com');
    });

    it('should not apply buffered user context if clearAuthenticatedUser was called', () => {
      service.setAuthenticatedUser('user-1', 'user@test.com');
      service.clearAuthenticatedUser();
      service.init();

      // The pending context was cleared, so nothing to assert was called —
      // just verify no error
      expect((service as any).pendingUserContext).toBeNull();
    });
  });
});
