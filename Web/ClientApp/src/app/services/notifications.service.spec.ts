import { fakeAsync, flushMicrotasks, TestBed, tick } from '@angular/core/testing';
import { HttpClientTestingModule, HttpTestingController } from '@angular/common/http/testing';
import { BehaviorSubject } from 'rxjs';
import * as SignalR from '@microsoft/signalr';
import { OAuthService } from 'angular-oauth2-oidc';
import { ApplicationState, applicationState, initialStateValue } from '../state';
import { ApiUser } from '../models';
import { MockAuthTokenService } from './mock-auth-token.service';
import { AppNotification, NotificationsService } from './notifications.service';

function userWithRoles(roles: string[]): ApiUser {
  return {
    uniqueId: 'u-1',
    createdTime: '',
    modifiedTime: '',
    creatorId: '',
    modifierId: '',
    givenName: 'A',
    surname: 'B',
    displayName: 'A B',
    identityProvider: 'idp',
    email: 'a@b.c',
    streetAddress: '',
    roles,
    ownedHomes: [],
  };
}

function notification(overrides: Partial<AppNotification>): AppNotification {
  return {
    id: 'n-1',
    type: 'Registration',
    targetType: 'user',
    targetId: 't-1',
    title: 'Title',
    summary: 'Summary',
    deepLink: '/manage/users',
    createdUtc: '2025-01-01T00:00:00Z',
    ...overrides,
  };
}

/** Mock hub connection; recreated per test so spy call counts and start behavior never leak. */
interface MockConnection {
  on: jasmine.Spy;
  start: jasmine.Spy;
  stop: jasmine.Spy;
  onreconnected: jasmine.Spy;
  onclose: jasmine.Spy;
}

describe('NotificationsService', () => {
  let state$: BehaviorSubject<ApplicationState>;
  let oauthSpy: jasmine.SpyObj<OAuthService>;
  let httpMock: HttpTestingController;
  let connection: MockConnection;

  /** ES module exports cannot be spied; stub the builder chain so start() never hits Karma's server. */
  const hubProto = SignalR.HubConnectionBuilder.prototype;
  const originalWithUrl = hubProto.withUrl;
  const originalWithAutomaticReconnect = hubProto.withAutomaticReconnect;
  const originalBuild = hubProto.build;

  /** Invokes the most recently registered 'NotificationsChanged' hub handler. */
  function fireHubSignal(): void {
    const lastOn = connection.on.calls.mostRecent();
    expect(lastOn.args[0]).toBe('NotificationsChanged');
    (lastOn.args[1] as () => void)();
  }

  /** Invokes the most recently registered handler passed to a connection lifecycle spy. */
  function fireLifecycle(spy: jasmine.Spy): void {
    (spy.calls.mostRecent().args[0] as () => void)();
  }

  beforeAll(() => {
    spyOn(hubProto, 'withUrl').and.callFake(function (this: SignalR.HubConnectionBuilder) {
      return this;
    });
    spyOn(hubProto, 'withAutomaticReconnect').and.callFake(function (this: SignalR.HubConnectionBuilder) {
      return this;
    });
    // Return whatever `connection` points at when build() runs, so each test gets fresh spies.
    spyOn(hubProto, 'build').and.callFake(() => connection as unknown as SignalR.HubConnection);
  });

  afterAll(() => {
    hubProto.withUrl = originalWithUrl;
    hubProto.withAutomaticReconnect = originalWithAutomaticReconnect;
    hubProto.build = originalBuild;
  });

  beforeEach(() => {
    connection = {
      on: jasmine.createSpy('hubOn'),
      start: jasmine.createSpy('hubStart').and.returnValue(Promise.resolve()),
      stop: jasmine.createSpy('hubStop').and.returnValue(Promise.resolve()),
      onreconnected: jasmine.createSpy('hubOnReconnected'),
      onclose: jasmine.createSpy('hubOnClose'),
    };

    state$ = new BehaviorSubject<ApplicationState>({ ...initialStateValue, apiUser: null });
    oauthSpy = jasmine.createSpyObj('OAuthService', ['getAccessToken']);
    oauthSpy.getAccessToken.and.returnValue('');

    TestBed.configureTestingModule({
      imports: [HttpClientTestingModule],
      providers: [
        NotificationsService,
        { provide: OAuthService, useValue: oauthSpy },
        MockAuthTokenService,
        { provide: applicationState, useValue: state$.asObservable() },
      ],
    });
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('does not fetch for a user with no notification audience', () => {
    TestBed.inject(NotificationsService);
    state$.next({ ...initialStateValue, apiUser: userWithRoles(['Resident']) });

    httpMock.expectNone('api/notifications');
  });

  it('fetches and sorts newest-first when the user gains an audience', () => {
    const service = TestBed.inject(NotificationsService);
    state$.next({ ...initialStateValue, apiUser: userWithRoles(['Administrator']) });

    const req = httpMock.expectOne('api/notifications');
    expect(req.request.method).toBe('GET');
    req.flush([
      notification({ id: 'old', createdUtc: '2025-01-01T00:00:00Z' }),
      notification({ id: 'new', createdUtc: '2025-02-01T00:00:00Z' }),
    ]);

    let list: AppNotification[] = [];
    let unread = -1;
    service.notifications$.subscribe(n => (list = n));
    service.unreadCount$.subscribe(c => (unread = c));

    expect(list.map(n => n.id)).toEqual(['new', 'old']);
    expect(unread).toBe(2);
  });

  it('acknowledge posts and optimistically removes the notification', () => {
    const service = TestBed.inject(NotificationsService);
    state$.next({ ...initialStateValue, apiUser: userWithRoles(['Administrator']) });
    httpMock.expectOne('api/notifications').flush([notification({ id: 'ack-me' }), notification({ id: 'keep' })]);

    service.acknowledge('ack-me').subscribe();
    const ackReq = httpMock.expectOne('api/notifications/ack-me/acknowledge');
    expect(ackReq.request.method).toBe('POST');
    ackReq.flush(null);

    let list: AppNotification[] = [];
    service.notifications$.subscribe(n => (list = n));
    expect(list.map(n => n.id)).toEqual(['keep']);
  });

  it('unacknowledge posts and optimistically re-inserts the notification in sorted position', () => {
    const service = TestBed.inject(NotificationsService);
    state$.next({ ...initialStateValue, apiUser: userWithRoles(['Administrator']) });
    httpMock.expectOne('api/notifications').flush([
      notification({ id: 'newest', createdUtc: '2025-03-01T00:00:00Z' }),
      notification({ id: 'oldest', createdUtc: '2025-01-01T00:00:00Z' }),
    ]);

    const restored = notification({ id: 'restore-me', createdUtc: '2025-02-01T00:00:00Z' });
    service.unacknowledge(restored).subscribe();
    const req = httpMock.expectOne('api/notifications/restore-me/unacknowledge');
    expect(req.request.method).toBe('POST');
    req.flush(null);

    let list: AppNotification[] = [];
    service.notifications$.subscribe(n => (list = n));
    expect(list.map(n => n.id)).toEqual(['newest', 'restore-me', 'oldest']);
  });

  it('an unacknowledge response landing after teardown does not repopulate cleared state', () => {
    const service = TestBed.inject(NotificationsService);
    state$.next({ ...initialStateValue, apiUser: userWithRoles(['Administrator']) });
    const item = notification({ id: 'gone' });
    httpMock.expectOne('api/notifications').flush([item]);

    service.acknowledge('gone').subscribe();
    httpMock.expectOne('api/notifications/gone/acknowledge').flush(null);

    service.unacknowledge(item).subscribe();
    // Rights are revoked while the undo POST is in flight — teardown clears the list.
    state$.next({ ...initialStateValue, apiUser: userWithRoles(['Resident']) });
    httpMock.expectOne('api/notifications/gone/unacknowledge').flush(null);

    let list: AppNotification[] = [];
    let unread = -1;
    service.notifications$.subscribe(n => (list = n));
    service.unreadCount$.subscribe(c => (unread = c));
    expect(list).toEqual([]);
    expect(unread).toBe(0);
  });

  it('an unacknowledge response from before a teardown does not insert stale state after re-initialize', () => {
    const service = TestBed.inject(NotificationsService);
    state$.next({ ...initialStateValue, apiUser: userWithRoles(['Administrator']) });
    const stale = notification({ id: 'stale' });
    httpMock.expectOne('api/notifications').flush([stale]);

    service.acknowledge('stale').subscribe();
    httpMock.expectOne('api/notifications/stale/acknowledge').flush(null);

    service.unacknowledge(stale).subscribe();
    // Rights are revoked and then restored while the undo POST is in flight — the service tears
    // down and re-initializes, so `active` is true again when the old session's response lands.
    state$.next({ ...initialStateValue, apiUser: userWithRoles(['Resident']) });
    state$.next({ ...initialStateValue, apiUser: userWithRoles(['Administrator']) });
    const fresh = notification({ id: 'fresh' });
    httpMock.expectOne('api/notifications').flush([fresh]);
    httpMock.expectOne('api/notifications/stale/unacknowledge').flush(null);

    let list: AppNotification[] = [];
    service.notifications$.subscribe(n => (list = n));
    expect(list.map(n => n.id)).toEqual(['fresh']);
  });

  it(`an acknowledge response from before a teardown does not invalidate the new session's initial fetch`, () => {
    const service = TestBed.inject(NotificationsService);
    state$.next({ ...initialStateValue, apiUser: userWithRoles(['Administrator']) });
    httpMock.expectOne('api/notifications').flush([notification({ id: 'old-session' })]);

    service.acknowledge('old-session').subscribe();
    // Teardown + re-initialize while the acknowledge POST is in flight. The new session's initial
    // fetch is still pending when the stale response arrives; a generation bump from that response
    // would silently drop the fetch's payload with no retry scheduled.
    state$.next({ ...initialStateValue, apiUser: userWithRoles(['Resident']) });
    state$.next({ ...initialStateValue, apiUser: userWithRoles(['Administrator']) });
    const pendingFetch = httpMock.expectOne('api/notifications');
    httpMock.expectOne('api/notifications/old-session/acknowledge').flush(null);
    const fresh = notification({ id: 'fresh' });
    pendingFetch.flush([fresh]);

    let list: AppNotification[] = [];
    service.notifications$.subscribe(n => (list = n));
    expect(list.map(n => n.id)).toEqual(['fresh']);
  });

  it('unacknowledge does not duplicate a notification a refetch already restored', () => {
    const service = TestBed.inject(NotificationsService);
    state$.next({ ...initialStateValue, apiUser: userWithRoles(['Administrator']) });
    const restored = notification({ id: 'already-back' });
    httpMock.expectOne('api/notifications').flush([restored]);

    service.unacknowledge(restored).subscribe();
    httpMock.expectOne('api/notifications/already-back/unacknowledge').flush(null);

    let list: AppNotification[] = [];
    service.notifications$.subscribe(n => (list = n));
    expect(list.map(n => n.id)).toEqual(['already-back']);
  });

  it('a refresh already in flight when acknowledge runs cannot resurrect the acknowledged item', fakeAsync(() => {
    const service = TestBed.inject(NotificationsService);
    state$.next({ ...initialStateValue, apiUser: userWithRoles(['Administrator']) });
    httpMock.expectOne('api/notifications').flush([notification({ id: 'ack-me' }), notification({ id: 'keep' })]);

    let list: AppNotification[] = [];
    service.notifications$.subscribe(n => (list = n));

    // A hub signal kicks off a debounced re-fetch that is still in flight when the user acknowledges.
    fireHubSignal();
    tick(400);
    const inFlight = httpMock.expectOne('api/notifications');

    service.acknowledge('ack-me').subscribe();
    httpMock.expectOne('api/notifications/ack-me/acknowledge').flush(null);
    expect(list.map(n => n.id)).toEqual(['keep']);

    // The stale (pre-acknowledge) response must be ignored, not re-applied.
    inFlight.flush([notification({ id: 'ack-me' }), notification({ id: 'keep' })]);
    expect(list.map(n => n.id)).toEqual(['keep']);
  }));

  it('clears state and stops fetching when the audience is lost', () => {
    const service = TestBed.inject(NotificationsService);
    state$.next({ ...initialStateValue, apiUser: userWithRoles(['Administrator']) });
    httpMock.expectOne('api/notifications').flush([notification({ id: 'x' })]);

    state$.next({ ...initialStateValue, apiUser: userWithRoles(['Resident']) });

    let list: AppNotification[] = [];
    service.notifications$.subscribe(n => (list = n));
    expect(list).toEqual([]);
    httpMock.expectNone('api/notifications');
  });

  it('tolerates a single 403 (keeps the badge) and recovers on retry without tearing down', fakeAsync(() => {
    const service = TestBed.inject(NotificationsService);
    state$.next({ ...initialStateValue, apiUser: userWithRoles(['Administrator']) });
    httpMock.expectOne('api/notifications').flush([notification({ id: 'x' })]);

    let list: AppNotification[] = [];
    service.notifications$.subscribe(n => (list = n));
    expect(list.map(n => n.id)).toEqual(['x']);

    // One 403 is treated as a transient token blip — the badge must NOT flicker to empty.
    fireHubSignal();
    tick(400);
    httpMock.expectOne('api/notifications').flush('Forbidden', { status: 403, statusText: 'Forbidden' });
    expect(list.map(n => n.id)).toEqual(['x']);

    // The backoff retry succeeds — state is unchanged, no teardown happened.
    tick(2000);
    httpMock.expectOne('api/notifications').flush([notification({ id: 'x' })]);
    expect(list.map(n => n.id)).toEqual(['x']);
  }));

  it('clears the list once a second consecutive 403 confirms a persistent authorization loss', fakeAsync(() => {
    const service = TestBed.inject(NotificationsService);
    state$.next({ ...initialStateValue, apiUser: userWithRoles(['Administrator']) });
    httpMock.expectOne('api/notifications').flush([notification({ id: 'x' })]);

    let list: AppNotification[] = [];
    service.notifications$.subscribe(n => (list = n));

    // First 403 is tolerated...
    fireHubSignal();
    tick(400);
    httpMock.expectOne('api/notifications').flush('Forbidden', { status: 403, statusText: 'Forbidden' });
    expect(list.map(n => n.id)).toEqual(['x']);

    // ...the retry is also 403, so the loss is treated as persistent and the stale items are cleared.
    tick(2000);
    httpMock.expectOne('api/notifications').flush('Forbidden', { status: 403, statusText: 'Forbidden' });
    expect(list).toEqual([]);

    // Losing the audience tears down and cancels the still-pending backoff retry.
    state$.next({ ...initialStateValue, apiUser: userWithRoles(['Resident']) });
  }));

  it('keeps the current list on a transient 5xx and retries', fakeAsync(() => {
    const service = TestBed.inject(NotificationsService);
    state$.next({ ...initialStateValue, apiUser: userWithRoles(['Administrator']) });
    httpMock.expectOne('api/notifications').flush([notification({ id: 'x' })]);

    let list: AppNotification[] = [];
    service.notifications$.subscribe(n => (list = n));
    expect(list.map(n => n.id)).toEqual(['x']);

    // A 5xx blip must not blank the badge.
    fireHubSignal();
    tick(400);
    httpMock.expectOne('api/notifications').flush('boom', { status: 503, statusText: 'Service Unavailable' });
    expect(list.map(n => n.id)).toEqual(['x']);

    tick(2000);
    httpMock.expectOne('api/notifications').flush([notification({ id: 'x' })]);
    expect(list.map(n => n.id)).toEqual(['x']);
  }));

  it('retries the initial hub connect after a transient failure', fakeAsync(() => {
    connection.start.and.returnValues(Promise.reject(new Error('negotiate failed')), Promise.resolve());

    TestBed.inject(NotificationsService);
    state$.next({ ...initialStateValue, apiUser: userWithRoles(['Administrator']) });
    httpMock.expectOne('api/notifications').flush([]);

    // Initial start() was attempted and rejected.
    flushMicrotasks();
    expect(connection.start).toHaveBeenCalledTimes(1);

    // Backoff retry re-attempts the connect rather than giving up for the session.
    tick(2000);
    flushMicrotasks();
    expect(connection.start).toHaveBeenCalledTimes(2);
  }));

  it('reconnects after onclose fires (auto-reconnect exhausted) rather than going silent', fakeAsync(() => {
    TestBed.inject(NotificationsService);
    state$.next({ ...initialStateValue, apiUser: userWithRoles(['Administrator']) });
    httpMock.expectOne('api/notifications').flush([]);
    flushMicrotasks();
    expect(connection.start).toHaveBeenCalledTimes(1);

    // SignalR gave up after its own reconnect window and fired onclose — we must restart.
    fireLifecycle(connection.onclose);
    tick(2000);
    flushMicrotasks();
    expect(connection.start).toHaveBeenCalledTimes(2);
  }));

  it('refetches the list when the hub reconnects to reconcile signals missed during the drop', fakeAsync(() => {
    const service = TestBed.inject(NotificationsService);
    state$.next({ ...initialStateValue, apiUser: userWithRoles(['Administrator']) });
    httpMock.expectOne('api/notifications').flush([]);
    flushMicrotasks();

    let list: AppNotification[] = [];
    service.notifications$.subscribe(n => (list = n));
    expect(list).toEqual([]);

    // A NotificationsChanged signal may have been missed while the connection was down.
    fireLifecycle(connection.onreconnected);
    httpMock.expectOne('api/notifications').flush([notification({ id: 'missed' })]);
    expect(list.map(n => n.id)).toEqual(['missed']);
  }));
});
