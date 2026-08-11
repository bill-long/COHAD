import { BreakpointObserver } from '@angular/cdk/layout';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, NavigationEnd, Router } from '@angular/router';
import { BehaviorSubject, Observable, Subject, of } from 'rxjs';
import { ManageComponent } from './manage.component';
import { AppNotification, NotificationsService } from 'src/app/services/notifications.service';
import { ApplicationState, applicationState, initialStateValue } from 'src/app/state';

/** Reads the current synchronous value of a BehaviorSubject-backed observable. */
function latest<T>(obs: Observable<T>): T {
  let value!: T;
  obs.subscribe(v => (value = v)).unsubscribe();
  return value;
}

function makeState(roles: string[] | null): ApplicationState {
  return {
    ...initialStateValue,
    apiUser: roles === null ? null : ({ uniqueId: 'u1', name: 'Test', email: 't@test.com', roles, homes: [] } as any),
  };
}

function makeNotification(type: AppNotification['type']): AppNotification {
  return {
    id: `n-${Math.random()}`,
    type,
    targetType: type,
    targetId: 'x',
    title: 't',
    summary: 's',
    deepLink: null,
    createdUtc: '2026-01-01T00:00:00Z',
  };
}

describe('ManageComponent', () => {
  let component: ManageComponent;
  let appStateSubject: BehaviorSubject<ApplicationState>;
  let notificationsSubject: BehaviorSubject<AppNotification[]>;
  let routerSpy: jasmine.SpyObj<Router>;
  let routerEvents: Subject<unknown>;

  function setupTestBed(state: ApplicationState, url = '/manage/users'): void {
    appStateSubject = new BehaviorSubject<ApplicationState>(state);
    notificationsSubject = new BehaviorSubject<AppNotification[]>([]);
    routerEvents = new Subject<unknown>();
    routerSpy = jasmine.createSpyObj<Router>('Router', ['navigate']);
    (routerSpy as any).url = url;
    (routerSpy as any).events = routerEvents.asObservable();

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        ManageComponent,
        { provide: applicationState, useValue: appStateSubject.asObservable() },
        { provide: NotificationsService, useValue: { notifications$: notificationsSubject.asObservable() } },
        { provide: BreakpointObserver, useValue: { observe: () => of({ matches: false, breakpoints: {} }) } },
        { provide: Router, useValue: routerSpy },
        { provide: ActivatedRoute, useValue: {} },
      ],
    });

    component = TestBed.inject(ManageComponent);
  }

  function visibleLabels(): { group: string; items: string[] }[] {
    return latest(component.visibleGroups$).map(g => ({ group: g.label, items: g.items.map(i => i.label) }));
  }

  it('shows all three groups and every tool for an Administrator', () => {
    setupTestBed(makeState(['Administrator', 'Resident']));
    expect(visibleLabels()).toEqual([
      { group: 'Directory', items: ['Users', 'Homes', 'Print Directory'] },
      { group: 'Communications', items: ['Email', 'Suppressions', 'News', 'Events', 'Documents'] },
      { group: 'Governance', items: ['Committees', 'Approvals', 'Audit Log'] },
    ]);
  });

  it('hides the admin-only Directory group and admin-only tools for a committee member', () => {
    setupTestBed(makeState(['Resident', 'WelcomeCommittee']));
    expect(visibleLabels()).toEqual([
      // No Directory group (Users/Homes/Print are Administrator-only).
      { group: 'Communications', items: ['Email', 'News', 'Events'] }, // Documents is Administrator-only
      { group: 'Governance', items: ['Committees', 'Approvals'] }, // Audit Log is Administrator-only
    ]);
  });

  it('hides News and Events for a committee role lacking the Resident role', () => {
    setupTestBed(makeState(['WelcomeCommittee']));
    const comms = visibleLabels().find(g => g.group === 'Communications');
    expect(comms?.items).toEqual(['Email']); // News + Events require Resident; Documents is admin-only
  });

  it('shows no groups when there is no signed-in user', () => {
    setupTestBed(makeState(null));
    expect(visibleLabels()).toEqual([]);
  });

  it('drops a whole group when none of its items are visible', () => {
    // ArchitecturalCommittee can see Committees/Approvals/Events/News but nothing in Directory.
    setupTestBed(makeState(['Resident', 'ArchitecturalCommittee']));
    expect(visibleLabels().some(g => g.group === 'Directory')).toBeFalse();
  });

  describe('Approvals badge count', () => {
    it('counts only HeldMessage notifications', () => {
      setupTestBed(makeState(['Administrator', 'Resident']));
      notificationsSubject.next([makeNotification('HeldMessage'), makeNotification('Registration'), makeNotification('HeldMessage')]);
      expect(latest(component.approvalsCount$)).toBe(2);
    });

    it('is zero when there are no held messages', () => {
      setupTestBed(makeState(['Administrator', 'Resident']));
      notificationsSubject.next([makeNotification('Registration'), makeNotification('VendorFlag')]);
      expect(latest(component.approvalsCount$)).toBe(0);
    });

    it('grafts the badge stream onto the Approvals rail item', () => {
      // The rail definition is a static exported const; the badge is attached per-instance by route
      // name in the constructor. This locks the graft so a renamed Approvals entry cannot silently
      // ship a rail without its held-message badge.
      setupTestBed(makeState(['Administrator', 'Resident']));
      let approvals;
      for (const group of component.groups) {
        approvals = group.items.find(item => item.label === 'Approvals') ?? approvals;
      }
      expect(approvals).withContext('Approvals rail item should exist').toBeDefined();
      expect(approvals!.badgeCount$).toBe(component.approvalsCount$);
    });
  });

  describe('default landing', () => {
    // Re-entry helper: simulates navigating (while the component stays mounted) by setting the URL and
    // emitting the matching NavigationEnd. Initial-landing tests deliberately do NOT use this — in
    // production the creating navigation's NavigationEnd fires before ngOnInit subscribes, so it is
    // never delivered here; the component's startWith(null) is what must handle the initial landing.
    function reEnter(url: string): void {
      (routerSpy as any).url = url;
      routerEvents.next(new NavigationEnd(1, url, url));
    }

    it('redirects an initial landing on /manage to the first accessible tool (no NavigationEnd delivered)', () => {
      setupTestBed(makeState(['Resident', 'WelcomeCommittee']), '/manage');
      component.ngOnInit();
      // No NavigationEnd is emitted — this models the real ordering where the creating navigation's
      // event already fired. First visible group for a committee member is Communications → Email.
      expect(routerSpy.navigate).toHaveBeenCalledWith(['send-email'], jasmine.objectContaining({ replaceUrl: true }));
    });

    it('redirects an Administrator landing on /manage to Users', () => {
      setupTestBed(makeState(['Administrator', 'Resident']), '/manage');
      component.ngOnInit();
      expect(routerSpy.navigate).toHaveBeenCalledWith(['users'], jasmine.objectContaining({ replaceUrl: true }));
    });

    it('does not redirect when a tool is already selected', () => {
      setupTestBed(makeState(['Administrator', 'Resident']), '/manage/homes');
      component.ngOnInit();
      expect(routerSpy.navigate).not.toHaveBeenCalled();
    });

    it('redirects on re-entry to bare /manage from a child route (component reused)', () => {
      // Start on a child route: no redirect on init.
      setupTestBed(makeState(['Administrator', 'Resident']), '/manage/homes');
      component.ngOnInit();
      expect(routerSpy.navigate).not.toHaveBeenCalled();

      // User navigates back to bare /manage; the parent component is reused, so only a
      // NavigationEnd — not a fresh ngOnInit — signals the re-entry.
      reEnter('/manage');
      expect(routerSpy.navigate).toHaveBeenCalledWith(['users'], jasmine.objectContaining({ replaceUrl: true }));
    });

    it('does not yank the user if the profile resolves after they leave /manage', () => {
      // Bare /manage, but the user has no profile yet (empty groups → redirect is deferred).
      setupTestBed(makeState(null), '/manage');
      component.ngOnInit();
      expect(routerSpy.navigate).not.toHaveBeenCalled();

      // User navigates to a child route before the profile loads...
      (routerSpy as any).url = '/manage/homes';
      // ...then the profile arrives. The deferred redirect must see it's no longer on bare /manage.
      appStateSubject.next(makeState(['Administrator', 'Resident']));
      expect(routerSpy.navigate).not.toHaveBeenCalled();
    });

    it('does not redirect when there is no visible tool for the user', () => {
      setupTestBed(makeState(null), '/manage');
      component.ngOnInit();
      expect(routerSpy.navigate).not.toHaveBeenCalled();
    });
  });
});
