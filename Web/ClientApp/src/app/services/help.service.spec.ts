import { TestBed } from '@angular/core/testing';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideHttpClient } from '@angular/common/http';
import { NavigationEnd, Router } from '@angular/router';
import { BehaviorSubject, Subject } from 'rxjs';

import { HelpService } from './help.service';
import { ApplicationState, applicationState, initialStateValue } from '../state';
import { ApiUser } from '../models';

function makeApiUser(roles: string[]): ApiUser {
  return {
    uniqueId: 'u-1',
    createdTime: '',
    modifiedTime: '',
    creatorId: '',
    modifierId: '',
    givenName: 'Test',
    surname: 'User',
    displayName: 'Test User',
    identityProvider: 'idp',
    email: 'test@example.com',
    streetAddress: '',
    roles,
    ownedHomes: [],
  };
}

describe('HelpService', () => {
  let appState$: BehaviorSubject<ApplicationState>;
  let routerEvents$: Subject<unknown>;
  let service: HelpService;
  let httpMock: HttpTestingController;

  // The single place the testing module is defined; the seeding test re-runs it with a different
  // router stub, so a new HelpService dependency only ever has to be added here.
  function configureHelpModule(routerStub: { events: unknown; url: string }): void {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: Router, useValue: routerStub },
        { provide: applicationState, useValue: appState$.asObservable() },
      ],
    });
  }

  beforeEach(() => {
    appState$ = new BehaviorSubject<ApplicationState>(initialStateValue);
    routerEvents$ = new Subject<unknown>();

    configureHelpModule({ events: routerEvents$.asObservable(), url: '/' });

    service = TestBed.inject(HelpService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  function setRoles(roles: string[] | null): void {
    appState$.next({ ...initialStateValue, apiUser: roles === null ? null : makeApiUser(roles) });
  }

  function visibleTopicIds(): string[] {
    let ids: string[] = [];
    service.visibleTopics$.subscribe(topics => (ids = topics.map(t => t.id))).unsubscribe();
    return ids;
  }

  describe('visibleTopics$', () => {
    it('shows every topic to an Administrator', () => {
      setRoles(['Administrator', 'Resident']);
      expect(visibleTopicIds()).toContain('users');
      expect(visibleTopicIds()).toContain('suppressions');
      expect(visibleTopicIds()).toContain('new-administrator');
    });

    it('hides Administrator-only topics from a committee-only user', () => {
      setRoles(['GardenClub', 'Resident']);
      const ids = visibleTopicIds();
      expect(ids).toContain('new-administrator');
      expect(ids).toContain('send-email');
      expect(ids).toContain('approvals');
      expect(ids).not.toContain('users');
      expect(ids).not.toContain('suppressions');
      expect(ids).not.toContain('audit-log');
      // Print Directory follows the rail's Administrator-only gate, not the route's broader roles.
      expect(ids).not.toContain('print');
    });

    it('hides Resident-required topics from a committee member without the Resident role', () => {
      setRoles(['GardenClub']);
      const ids = visibleTopicIds();
      expect(ids).toContain('send-email');
      expect(ids).not.toContain('events');
      expect(ids).not.toContain('blog');
    });

    it('is empty for a signed-out or role-less user', () => {
      setRoles(null);
      expect(visibleTopicIds()).toEqual([]);
      setRoles(['Resident']);
      expect(visibleTopicIds()).toEqual([]);
    });
  });

  describe('currentTopic', () => {
    it('seeds from the current URL at construction, before any NavigationEnd', () => {
      // A service instantiated after the navigation that would have reported the route must still
      // know the contextual topic immediately.
      TestBed.resetTestingModule();
      configureHelpModule({ events: new Subject().asObservable(), url: '/manage/users' });
      const seeded = TestBed.inject(HelpService);
      // Re-point the shared controller at the re-created module so the afterEach verify() checks
      // THIS module's requests instead of vacuously passing against the destroyed one.
      httpMock = TestBed.inject(HttpTestingController);
      expect(seeded.currentTopic?.id).toBe('users');
    });

    it('follows router navigation', () => {
      expect(service.currentTopic).toBeNull();
      routerEvents$.next(new NavigationEnd(1, '/manage/users', '/manage/users'));
      expect(service.currentTopic?.id).toBe('users');
      routerEvents$.next(new NavigationEnd(2, '/residents/directory', '/residents/directory'));
      expect(service.currentTopic).toBeNull();
    });
  });

  describe('getTopicHtml', () => {
    it('fetches and renders the markdown doc', () => {
      let html: unknown;
      service.getTopicHtml('users').subscribe(h => (html = h));
      httpMock.expectOne('assets/help/users.md').flush('# Users\n\nHello.');
      expect(html).not.toBeNull();
      expect(String(html)).toContain('<h1>');
    });

    it('caches per topic so a second subscription makes no second request', () => {
      service.getTopicHtml('users').subscribe();
      httpMock.expectOne('assets/help/users.md').flush('# Users');
      let html: unknown;
      service.getTopicHtml('users').subscribe(h => (html = h));
      // No expectOne: httpMock.verify() in afterEach fails this test if a second request went out.
      expect(html).not.toBeNull();
    });

    it('yields null on a failed fetch and retries on the next call', () => {
      let html: unknown = 'unset';
      service.getTopicHtml('users').subscribe(h => (html = h));
      httpMock.expectOne('assets/help/users.md').flush('missing', { status: 404, statusText: 'Not Found' });
      expect(html).toBeNull();

      service.getTopicHtml('users').subscribe(h => (html = h));
      httpMock.expectOne('assets/help/users.md').flush('# Users');
      expect(html).not.toBeNull();
    });
  });
});
