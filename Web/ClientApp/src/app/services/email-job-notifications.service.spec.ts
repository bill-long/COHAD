import { TestBed } from '@angular/core/testing';
import * as SignalR from '@microsoft/signalr';
import { OAuthService } from 'angular-oauth2-oidc';
import { MockAuthTokenService } from './mock-auth-token.service';
import { EmailJobNotificationsService } from './email-job-notifications.service';
import { EmailJobProgress, EmailJobCompleted } from '../models';

describe('EmailJobNotificationsService', () => {
  let oauthSpy: jasmine.SpyObj<OAuthService>;
  let hubConnection: {
    on: jasmine.Spy;
    start: jasmine.Spy;
    stop: jasmine.Spy;
  };

  const hubProto = SignalR.HubConnectionBuilder.prototype;
  const originalWithUrl = hubProto.withUrl;
  const originalWithAutomaticReconnect = hubProto.withAutomaticReconnect;
  const originalBuild = hubProto.build;

  beforeAll(() => {
    hubConnection = {
      on: jasmine.createSpy('hubOn'),
      start: jasmine.createSpy('hubStart').and.returnValue(Promise.resolve()),
      stop: jasmine.createSpy('hubStop').and.returnValue(Promise.resolve()),
    };
    spyOn(hubProto, 'withUrl').and.callFake(function (this: SignalR.HubConnectionBuilder) {
      return this;
    });
    spyOn(hubProto, 'withAutomaticReconnect').and.callFake(function (this: SignalR.HubConnectionBuilder) {
      return this;
    });
    spyOn(hubProto, 'build').and.returnValue(hubConnection as unknown as SignalR.HubConnection);
  });

  afterAll(() => {
    hubProto.withUrl = originalWithUrl;
    hubProto.withAutomaticReconnect = originalWithAutomaticReconnect;
    hubProto.build = originalBuild;
  });

  beforeEach(() => {
    hubConnection.on.calls.reset();
    hubConnection.start.calls.reset();
    hubConnection.stop.calls.reset();
    hubConnection.start.and.returnValue(Promise.resolve());

    oauthSpy = jasmine.createSpyObj('OAuthService', ['getAccessToken']);
    oauthSpy.getAccessToken.and.returnValue('test-token');

    TestBed.configureTestingModule({
      providers: [EmailJobNotificationsService, { provide: OAuthService, useValue: oauthSpy }, MockAuthTokenService],
    });
  });

  it('registers hub event handlers and starts connection on connect()', () => {
    const service = TestBed.inject(EmailJobNotificationsService);
    service.connect();

    expect(hubConnection.on).toHaveBeenCalledWith('EmailJobProgress', jasmine.any(Function));
    expect(hubConnection.on).toHaveBeenCalledWith('EmailJobCompleted', jasmine.any(Function));
    expect(hubConnection.start).toHaveBeenCalledTimes(1);
  });

  it('does not start a second connection if already connected', async () => {
    const service = TestBed.inject(EmailJobNotificationsService);
    service.connect();
    await Promise.resolve(); // let start() resolve
    service.connect();

    expect(hubConnection.start).toHaveBeenCalledTimes(1);
  });

  it('emits progress events on the progress$ observable', () => {
    const service = TestBed.inject(EmailJobNotificationsService);
    service.connect();

    const progressHandler = hubConnection.on.calls.allArgs().find((args: any[]) => args[0] === 'EmailJobProgress')![1] as (
      e: EmailJobProgress,
    ) => void;

    const event: EmailJobProgress = {
      jobId: 'j1',
      status: 'InProgress',
      sentCount: 5,
      failedCount: 0,
      suppressedCount: 0,
      totalRecipients: 10,
    };

    let received: EmailJobProgress | undefined;
    service.progress$.subscribe(e => (received = e));
    progressHandler(event);

    expect(received).toEqual(event);
  });

  it('emits completed events on the completed$ observable', () => {
    const service = TestBed.inject(EmailJobNotificationsService);
    service.connect();

    const completedHandler = hubConnection.on.calls.allArgs().find((args: any[]) => args[0] === 'EmailJobCompleted')![1] as (
      e: EmailJobCompleted,
    ) => void;

    const event: EmailJobCompleted = {
      jobId: 'j1',
      status: 'Completed',
      sentCount: 10,
      failedCount: 0,
      suppressedCount: 0,
      totalRecipients: 10,
      lastError: null,
    };

    let received: EmailJobCompleted | undefined;
    service.completed$.subscribe(e => (received = e));
    completedHandler(event);

    expect(received).toEqual(event);
  });

  it('stops the connection on disconnect()', async () => {
    const service = TestBed.inject(EmailJobNotificationsService);
    service.connect();
    await Promise.resolve();
    service.disconnect();

    expect(hubConnection.stop).toHaveBeenCalled();
  });
});
