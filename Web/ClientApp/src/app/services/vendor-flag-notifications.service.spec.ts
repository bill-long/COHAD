import { TestBed } from '@angular/core/testing';
import { BehaviorSubject, of } from 'rxjs';
import * as SignalR from '@microsoft/signalr';
import { OAuthService } from 'angular-oauth2-oidc';
import { ApplicationState, applicationState, initialStateValue } from '../state';
import { ApiUser } from '../models';
import { MockAuthTokenService } from './mock-auth-token.service';
import { VendorFlagNotification, VendorsService } from './vendors.service';
import { VendorFlagNotificationsService } from './vendor-flag-notifications.service';

function minimalAdmin(): ApiUser {
  return {
    uniqueId: 'admin-1',
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
    roles: ['Administrator'],
    ownedHomes: []
  };
}

describe('VendorFlagNotificationsService', () => {
  let vendorsSpy: jasmine.SpyObj<VendorsService>;
  let state$: BehaviorSubject<ApplicationState>;
  let oauthSpy: jasmine.SpyObj<OAuthService>;

  /** ES module exports cannot be spied; stub the builder chain so start() never hits Karma's server. */
  const hubProto = SignalR.HubConnectionBuilder.prototype;
  const originalWithUrl = hubProto.withUrl;
  const originalWithAutomaticReconnect = hubProto.withAutomaticReconnect;
  const originalBuild = hubProto.build;

  beforeAll(() => {
    const connection = {
      on: jasmine.createSpy('hubOn'),
      start: jasmine.createSpy('hubStart').and.returnValue(Promise.resolve()),
      stop: jasmine.createSpy('hubStop').and.returnValue(Promise.resolve())
    };
    spyOn(hubProto, 'withUrl').and.callFake(function (this: SignalR.HubConnectionBuilder) {
      return this;
    });
    spyOn(hubProto, 'withAutomaticReconnect').and.callFake(function (this: SignalR.HubConnectionBuilder) {
      return this;
    });
    spyOn(hubProto, 'build').and.returnValue(connection as unknown as SignalR.HubConnection);
  });

  afterAll(() => {
    hubProto.withUrl = originalWithUrl;
    hubProto.withAutomaticReconnect = originalWithAutomaticReconnect;
    hubProto.build = originalBuild;
  });

  beforeEach(() => {
    state$ = new BehaviorSubject<ApplicationState>({
      ...initialStateValue,
      apiUser: null
    });
    vendorsSpy = jasmine.createSpyObj('VendorsService', ['getPendingFlagNotifications']);
    vendorsSpy.getPendingFlagNotifications.and.returnValue(of([]));
    oauthSpy = jasmine.createSpyObj('OAuthService', ['getAccessToken']);
    oauthSpy.getAccessToken.and.returnValue('');

    TestBed.configureTestingModule({
      providers: [
        VendorFlagNotificationsService,
        { provide: VendorsService, useValue: vendorsSpy },
        { provide: OAuthService, useValue: oauthSpy },
        MockAuthTokenService,
        { provide: applicationState, useValue: state$.asObservable() }
      ]
    });
  });

  it('dedupes initial notifications so unread count matches displayed list', () => {
    const rows: VendorFlagNotification[] = [
      {
        flagId: 'f1',
        vendorId: 'v1',
        vendorName: 'Vendor',
        authorDisplayName: 'Auth',
        flagNote: 'first',
        createdUtc: '2025-01-02T00:00:00Z'
      },
      {
        flagId: 'f1',
        vendorId: 'v1',
        vendorName: 'Vendor',
        authorDisplayName: 'Auth',
        flagNote: 'second',
        createdUtc: '2025-01-01T00:00:00Z'
      }
    ];
    vendorsSpy.getPendingFlagNotifications.and.returnValue(of(rows));

    TestBed.inject(VendorFlagNotificationsService);
    state$.next({ ...initialStateValue, apiUser: minimalAdmin() });

    const service = TestBed.inject(VendorFlagNotificationsService);
    let list: VendorFlagNotification[] = [];
    let unread = -1;
    service.notifications$.subscribe(n => (list = n));
    service.unreadCount$.subscribe(c => (unread = c));

    expect(list.length).toBe(1);
    expect(list[0].flagNote).toBe('first');
    expect(unread).toBe(1);
  });

  it('removeNotification clears item and unread count', () => {
    vendorsSpy.getPendingFlagNotifications.and.returnValue(
      of([
        {
          flagId: 'f-del',
          vendorId: 'v1',
          vendorName: 'V',
          authorDisplayName: 'A',
          flagNote: 'note',
          createdUtc: '2025-01-01T00:00:00Z'
        }
      ])
    );

    TestBed.inject(VendorFlagNotificationsService);
    state$.next({ ...initialStateValue, apiUser: minimalAdmin() });

    const service = TestBed.inject(VendorFlagNotificationsService);
    service.removeNotification('f-del');

    let list: VendorFlagNotification[] = [];
    let unread = -1;
    service.notifications$.subscribe(n => (list = n));
    service.unreadCount$.subscribe(c => (unread = c));

    expect(list.length).toBe(0);
    expect(unread).toBe(0);
  });
});
