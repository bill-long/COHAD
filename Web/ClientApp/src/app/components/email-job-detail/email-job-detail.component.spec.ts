import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { BehaviorSubject, Subject, of, throwError } from 'rxjs';
import { EmailJobDetailComponent } from './email-job-detail.component';
import { EmailJobService } from 'src/app/services/email-job.service';
import { EmailJobNotificationsService } from 'src/app/services/email-job-notifications.service';
import {
  EmailJobDetail,
  EmailJobProgress,
  EmailJobCompleted,
  EmailDeliveryEventDetail,
  EmailJobRecipientDetail,
} from 'src/app/models';
import { ApplicationState, applicationState, initialStateValue } from 'src/app/state';

const makeRecipient = (overrides: Partial<EmailJobRecipientDetail> = {}): EmailJobRecipientDetail => ({
  email: 'a@test.com',
  status: 'Sent',
  sentUtc: '2026-01-01T00:00:00Z',
  error: null,
  deliveryStatus: 'Delivered',
  deliveryStatusUpdatedUtc: '2026-01-01T00:01:00Z',
  provider: 'SendGrid',
  ...overrides,
});

const makeDetail = (overrides: Partial<EmailJobDetail> = {}): EmailJobDetail => ({
  id: 'j1',
  status: 'Completed',
  category: 'Board',
  fromEmail: 'board@cohad.org',
  fromDisplay: 'COHAD Board',
  toDisplay: 'Board opt-in residents',
  originalSenderEmail: null,
  originalSenderDisplay: null,
  subject: 'Test subject',
  createdUtc: '2026-01-01T00:00:00Z',
  startedUtc: null,
  completedUtc: null,
  createdByDisplayName: 'Admin',
  totalRecipients: 10,
  sentCount: 10,
  failedCount: 0,
  lastError: null,
  recipients: [],
  ...overrides,
});

const makeEvent = (overrides: Partial<EmailDeliveryEventDetail> = {}): EmailDeliveryEventDetail => ({
  email: 'a@test.com',
  deliveryStatus: 'Delivered',
  provider: 'SendGrid',
  providerEventType: 'delivered',
  providerEventId: 'evt-1',
  providerMessageId: 'msg-1',
  receivedUtc: '2026-01-01T00:01:00Z',
  providerPayloadJson: null,
  ...overrides,
});

describe('EmailJobDetailComponent', () => {
  let component: EmailJobDetailComponent;
  let emailJobServiceSpy: jasmine.SpyObj<EmailJobService>;
  let notificationsSpy: jasmine.SpyObj<EmailJobNotificationsService>;
  let routerSpy: jasmine.SpyObj<Router>;
  let progressSubject: Subject<EmailJobProgress>;
  let completedSubject: Subject<EmailJobCompleted>;
  let appStateSubject: BehaviorSubject<ApplicationState>;

  function setupTestBed(state: ApplicationState = initialStateValue): void {
    progressSubject = new Subject<EmailJobProgress>();
    completedSubject = new Subject<EmailJobCompleted>();
    appStateSubject = new BehaviorSubject<ApplicationState>(state);

    emailJobServiceSpy = jasmine.createSpyObj('EmailJobService', ['getJob', 'retryJob', 'cancelJob', 'getDeliveryEvents']);
    notificationsSpy = jasmine.createSpyObj('EmailJobNotificationsService', ['connect', 'disconnect']);
    Object.defineProperty(notificationsSpy, 'progress$', { get: () => progressSubject.asObservable() });
    Object.defineProperty(notificationsSpy, 'completed$', { get: () => completedSubject.asObservable() });
    routerSpy = jasmine.createSpyObj('Router', ['navigate']);

    emailJobServiceSpy.getJob.and.returnValue(of(makeDetail()));
    emailJobServiceSpy.getDeliveryEvents.and.returnValue(of([]));

    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        EmailJobDetailComponent,
        { provide: EmailJobService, useValue: emailJobServiceSpy },
        { provide: EmailJobNotificationsService, useValue: notificationsSpy },
        { provide: Router, useValue: routerSpy },
        { provide: applicationState, useValue: appStateSubject.asObservable() },
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { paramMap: convertToParamMap({ id: 'j1' }) } },
        },
      ],
    });

    component = TestBed.inject(EmailJobDetailComponent);
  }

  beforeEach(() => setupTestBed());

  it('loads job on init', () => {
    component.ngOnInit();
    expect(emailJobServiceSpy.getJob).toHaveBeenCalledWith('j1');
    expect(component.job).toEqual(makeDetail());
    expect(component.loading).toBeFalse();
  });

  it('parties are empty before the job loads', () => {
    expect(component.parties.from).toBe('');
    expect(component.parties.to).toBe('');
    expect(component.parties.forwardedTo).toBeNull();
  });

  it('parties name the sending mailbox and audience for a message composed in COHAD', () => {
    component.ngOnInit();
    expect(component.parties.from).toBe('COHAD Board <board@cohad.org>');
    expect(component.parties.to).toBe('Board opt-in residents');
    expect(component.parties.forwardedTo).toBeNull();
  });

  it('parties name the resident, not the committee, on a forwarded job', () => {
    emailJobServiceSpy.getJob.and.returnValue(
      of(
        makeDetail({
          category: 'committee-forward',
          fromEmail: 'architectural@cohad.org',
          fromDisplay: 'Architectural Committee',
          toDisplay: 'Architectural Committee forwarding members',
          originalSenderEmail: 'jane@example.com',
          originalSenderDisplay: 'Jane Doe',
        }),
      ),
    );
    component.ngOnInit();

    expect(component.parties.from).toBe('Jane Doe <jane@example.com>');
    expect(component.parties.to).toBe('Architectural Committee <architectural@cohad.org>');
    expect(component.parties.forwardedTo).toBe('Architectural Committee forwarding members');
  });

  it('parties are recomputed only when the job object changes', () => {
    component.ngOnInit();
    const first = component.parties;
    expect(component.parties).toBe(first);

    progressSubject.next({ jobId: 'j1', status: 'InProgress', sentCount: 5, failedCount: 0, totalRecipients: 12 });

    expect(component.parties).not.toBe(first);
    expect(component.parties.to).toBe('Board opt-in residents');
  });

  it('goBack() navigates to /manage/send-email with jobs fragment and focus job', () => {
    component.ngOnInit();
    component.goBack();
    expect(routerSpy.navigate).toHaveBeenCalledWith(['/manage/send-email'], {
      queryParams: { focusJob: 'j1' },
      fragment: 'email-jobs',
    });
  });

  it('updates job on progress event for matching id', () => {
    component.ngOnInit();
    progressSubject.next({ jobId: 'j1', status: 'InProgress', sentCount: 5, failedCount: 0, totalRecipients: 10 });
    expect(component.job?.status).toBe('InProgress');
    expect(component.job?.sentCount).toBe(5);
  });

  it('ignores progress events for non-matching ids', () => {
    component.ngOnInit();
    progressSubject.next({ jobId: 'other', status: 'InProgress', sentCount: 5, failedCount: 0, totalRecipients: 10 });
    expect(component.job?.status).toBe('Completed');
  });

  it('disconnects on destroy', () => {
    component.ngOnInit();
    component.ngOnDestroy();
    expect(notificationsSpy.disconnect).toHaveBeenCalledTimes(1);
  });

  describe('admin detection', () => {
    it('sets isAdmin=true when apiUser has Administrator role', () => {
      setupTestBed({
        ...initialStateValue,
        apiUser: { uniqueId: 'u1', name: 'Admin', email: 'a@test.com', roles: ['Administrator', 'Resident'], homes: [] } as any,
      });
      component.ngOnInit();
      expect(component.isAdmin).toBeTrue();
    });

    it('sets isAdmin=false when apiUser lacks Administrator role', () => {
      setupTestBed({
        ...initialStateValue,
        apiUser: { uniqueId: 'u1', name: 'User', email: 'u@test.com', roles: ['Resident'], homes: [] } as any,
      });
      component.ngOnInit();
      expect(component.isAdmin).toBeFalse();
    });
  });

  describe('delivery events lazy loading', () => {
    beforeEach(() => {
      // Default to admin so toggle expansion is accepted
      setupTestBed({
        ...initialStateValue,
        apiUser: { uniqueId: 'u1', name: 'Admin', email: 'a@test.com', roles: ['Administrator'], homes: [] } as any,
      });
      emailJobServiceSpy.getJob.and.returnValue(of(makeDetail({ recipients: [makeRecipient()] })));
      emailJobServiceSpy.getDeliveryEvents.and.returnValue(of([makeEvent()]));
    });

    it('first expand fetches events without payload (includePayload=false)', () => {
      component.ngOnInit();
      component.toggleRecipientEvents('a@test.com');
      expect(emailJobServiceSpy.getDeliveryEvents).toHaveBeenCalledWith('j1', false);
      expect(component.payloadsLoaded).toBeFalse();
      expect(component.expandedRecipients.has('a@test.com')).toBeTrue();
    });

    it('does not refetch when toggling a second recipient if events already loaded', () => {
      emailJobServiceSpy.getJob.and.returnValue(of(makeDetail({
        recipients: [makeRecipient(), makeRecipient({ email: 'b@test.com' })],
      })));
      component.ngOnInit();
      component.toggleRecipientEvents('a@test.com');
      component.toggleRecipientEvents('b@test.com');
      expect(emailJobServiceSpy.getDeliveryEvents).toHaveBeenCalledTimes(1);
    });

    it('loadDeliveryEvents(true) fetches payloads and sets payloadsLoaded=true', () => {
      component.ngOnInit();
      component.toggleRecipientEvents('a@test.com');
      emailJobServiceSpy.getDeliveryEvents.calls.reset();
      emailJobServiceSpy.getDeliveryEvents.and.returnValue(of([makeEvent({ providerPayloadJson: '{"event":"delivered"}' })]));

      component.loadDeliveryEvents(true);

      expect(emailJobServiceSpy.getDeliveryEvents).toHaveBeenCalledWith('j1', true);
      expect(component.payloadsLoaded).toBeTrue();
      expect(component.payloadsLoading).toBeFalse();
    });

    it('keeps events table visible during payload load (separate spinner state)', () => {
      const delayed = new Subject<EmailDeliveryEventDetail[]>();
      component.ngOnInit();
      component.toggleRecipientEvents('a@test.com');
      // Pre-existing events should remain after triggering payload load
      const eventsBefore = component.deliveryEvents;
      emailJobServiceSpy.getDeliveryEvents.and.returnValue(delayed.asObservable());

      component.loadDeliveryEvents(true);

      expect(component.deliveryEventsLoading).toBeFalse();
      expect(component.payloadsLoading).toBeTrue();
      expect(component.deliveryEvents).toBe(eventsBefore);

      delayed.next([makeEvent({ providerPayloadJson: '{}' })]);
      expect(component.payloadsLoading).toBeFalse();
    });

    it('preserves payload mode when refreshing on job completion', () => {
      component.ngOnInit();
      component.toggleRecipientEvents('a@test.com');
      // Pretend the user clicked "Load raw payloads"
      component.payloadsLoaded = true;
      emailJobServiceSpy.getDeliveryEvents.calls.reset();
      // The completion handler will re-fetch the job and (because a recipient is expanded)
      // re-fetch delivery events. We must still pass includePayload=true.
      emailJobServiceSpy.getJob.and.returnValue(of(makeDetail({ status: 'Completed', recipients: [makeRecipient()] })));

      completedSubject.next({
        jobId: 'j1',
        status: 'Completed',
        sentCount: 10,
        failedCount: 0,
        totalRecipients: 10,
        lastError: null,
      });

      expect(emailJobServiceSpy.getDeliveryEvents).toHaveBeenCalledWith('j1', true);
    });

    it('does not refetch delivery events on completion when no recipients are expanded', () => {
      component.ngOnInit();
      emailJobServiceSpy.getDeliveryEvents.calls.reset();
      emailJobServiceSpy.getJob.and.returnValue(of(makeDetail({ status: 'Completed' })));

      completedSubject.next({
        jobId: 'j1',
        status: 'Completed',
        sentCount: 10,
        failedCount: 0,
        totalRecipients: 10,
        lastError: null,
      });

      expect(emailJobServiceSpy.getDeliveryEvents).not.toHaveBeenCalled();
    });

    it('surfaces an error message when delivery events fetch fails', () => {
      emailJobServiceSpy.getDeliveryEvents.and.returnValue(throwError(() => ({ status: 500 })));
      component.ngOnInit();
      component.toggleRecipientEvents('a@test.com');
      expect(component.deliveryEventsError).toBeTruthy();
      expect(component.deliveryEventsLoading).toBeFalse();
    });

    it('preserves payload mode when refresh races with an in-flight payload load', () => {
      const inflightPayloads = new Subject<EmailDeliveryEventDetail[]>();
      component.ngOnInit();
      component.toggleRecipientEvents('a@test.com');
      // User clicks "Load raw payloads" — request is in flight, payloadsLoaded is still false.
      emailJobServiceSpy.getDeliveryEvents.and.returnValue(inflightPayloads.asObservable());
      component.loadDeliveryEvents(true);
      expect(component.payloadsLoading).toBeTrue();
      expect(component.payloadsLoaded).toBeFalse();
      emailJobServiceSpy.getDeliveryEvents.calls.reset();

      // SignalR completion event arrives mid-flight.
      emailJobServiceSpy.getJob.and.returnValue(of(makeDetail({ status: 'Completed', recipients: [makeRecipient()] })));
      completedSubject.next({
        jobId: 'j1',
        status: 'Completed',
        sentCount: 10,
        failedCount: 0,
        totalRecipients: 10,
        lastError: null,
      });

      // The refresh must keep payload mode, not silently drop it.
      expect(emailJobServiceSpy.getDeliveryEvents).toHaveBeenCalledWith('j1', true);
    });

    it('keeps events visible and surfaces a payload-scoped error when payload re-fetch fails', () => {
      component.ngOnInit();
      component.toggleRecipientEvents('a@test.com');
      const eventsBefore = component.deliveryEvents;
      expect(eventsBefore).not.toBeNull();

      // Now simulate the payload re-fetch failing
      emailJobServiceSpy.getDeliveryEvents.and.returnValue(throwError(() => ({ status: 500 })));
      component.loadDeliveryEvents(true);

      // Events from the original load should still be visible
      expect(component.deliveryEvents).toBe(eventsBefore);
      // The payload-scoped error slot is populated, NOT the main events error slot
      expect(component.payloadsError).toBeTruthy();
      expect(component.deliveryEventsError).toBeNull();
      expect(component.payloadsLoading).toBeFalse();
    });

    it('toggleRecipientEvents is a no-op when not admin (defense in depth)', () => {
      // Reset to a non-admin app state
      setupTestBed({ ...initialStateValue, apiUser: null });
      component.ngOnInit();
      component.toggleRecipientEvents('a@test.com');
      expect(component.expandedRecipients.size).toBe(0);
      expect(emailJobServiceSpy.getDeliveryEvents).not.toHaveBeenCalled();
    });

    it('discards stale responses so an older non-payload fetch cannot overwrite a newer payload load', () => {
      component.ngOnInit();
      component.toggleRecipientEvents('a@test.com');
      // Initial events-only load resolved synchronously above; reset call tracking.
      emailJobServiceSpy.getDeliveryEvents.calls.reset();

      // Issue an events-only request that we'll resolve later (the "stale" one).
      const staleEventsOnly = new Subject<EmailDeliveryEventDetail[]>();
      emailJobServiceSpy.getDeliveryEvents.and.returnValue(staleEventsOnly.asObservable());
      component.loadDeliveryEvents(false);

      // Then issue a newer payload-loaded request that resolves first.
      const eventWithPayload = makeEvent({ providerPayloadJson: '{"raw":"yes"}' });
      emailJobServiceSpy.getDeliveryEvents.and.returnValue(of([eventWithPayload]));
      component.loadDeliveryEvents(true);

      // Newer request applied: payloadsLoaded is true, payload data is on screen.
      expect(component.payloadsLoaded).toBeTrue();
      expect(component.deliveryEvents).toEqual([eventWithPayload]);

      // Older events-only request now resolves AFTER the newer one.
      staleEventsOnly.next([makeEvent({ providerPayloadJson: null })]);

      // State must reflect the newer payload-loaded response, not the stale one.
      expect(component.payloadsLoaded).toBeTrue();
      expect(component.deliveryEvents).toEqual([eventWithPayload]);
    });

    it('discards stale error responses so an older fetch cannot clobber newer success state', () => {
      component.ngOnInit();
      component.toggleRecipientEvents('a@test.com');
      emailJobServiceSpy.getDeliveryEvents.calls.reset();

      // Stale request that will eventually error.
      const staleErr = new Subject<EmailDeliveryEventDetail[]>();
      emailJobServiceSpy.getDeliveryEvents.and.returnValue(staleErr.asObservable());
      component.loadDeliveryEvents(true);

      // Newer request that succeeds first.
      emailJobServiceSpy.getDeliveryEvents.and.returnValue(of([makeEvent()]));
      component.loadDeliveryEvents(true);

      expect(component.payloadsError).toBeNull();
      expect(component.deliveryEventsError).toBeNull();

      // Older request errors after the newer one already won — must be ignored.
      staleErr.error({ status: 500 });

      expect(component.payloadsError).toBeNull();
      expect(component.deliveryEventsError).toBeNull();
    });
  });
});
