import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { Subject, of } from 'rxjs';
import { EmailJobDetailComponent } from './email-job-detail.component';
import { EmailJobService } from 'src/app/services/email-job.service';
import { EmailJobNotificationsService } from 'src/app/services/email-job-notifications.service';
import { EmailJobDetail, EmailJobProgress, EmailJobCompleted } from 'src/app/models';

const makeDetail = (overrides: Partial<EmailJobDetail> = {}): EmailJobDetail => ({
  id: 'j1',
  status: 'Completed',
  category: 'Board',
  fromDisplay: 'COHAD Board',
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

describe('EmailJobDetailComponent', () => {
  let component: EmailJobDetailComponent;
  let emailJobServiceSpy: jasmine.SpyObj<EmailJobService>;
  let notificationsSpy: jasmine.SpyObj<EmailJobNotificationsService>;
  let routerSpy: jasmine.SpyObj<Router>;
  let progressSubject: Subject<EmailJobProgress>;
  let completedSubject: Subject<EmailJobCompleted>;

  beforeEach(() => {
    progressSubject = new Subject<EmailJobProgress>();
    completedSubject = new Subject<EmailJobCompleted>();

    emailJobServiceSpy = jasmine.createSpyObj('EmailJobService', ['getJob', 'retryJob', 'cancelJob']);
    notificationsSpy = jasmine.createSpyObj('EmailJobNotificationsService', ['connect', 'disconnect']);
    Object.defineProperty(notificationsSpy, 'progress$', { get: () => progressSubject.asObservable() });
    Object.defineProperty(notificationsSpy, 'completed$', { get: () => completedSubject.asObservable() });
    routerSpy = jasmine.createSpyObj('Router', ['navigate']);

    emailJobServiceSpy.getJob.and.returnValue(of(makeDetail()));

    TestBed.configureTestingModule({
      providers: [
        EmailJobDetailComponent,
        { provide: EmailJobService, useValue: emailJobServiceSpy },
        { provide: EmailJobNotificationsService, useValue: notificationsSpy },
        { provide: Router, useValue: routerSpy },
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { paramMap: convertToParamMap({ id: 'j1' }) } },
        },
      ],
    });

    component = TestBed.inject(EmailJobDetailComponent);
  });

  it('loads job on init', () => {
    component.ngOnInit();
    expect(emailJobServiceSpy.getJob).toHaveBeenCalledWith('j1');
    expect(component.job).toEqual(makeDetail());
    expect(component.loading).toBeFalse();
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
});
