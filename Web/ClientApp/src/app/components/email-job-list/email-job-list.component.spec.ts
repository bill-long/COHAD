import { Location } from '@angular/common';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { Subject, of, throwError } from 'rxjs';
import { EmailJobListComponent } from './email-job-list.component';
import { EmailJobService } from 'src/app/services/email-job.service';
import { EmailJobNotificationsService } from 'src/app/services/email-job-notifications.service';
import { EmailJobSummary, EmailJobProgress, EmailJobCompleted } from 'src/app/models';

const makeJob = (id: string, overrides: Partial<EmailJobSummary> = {}): EmailJobSummary => ({
  id,
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
  ...overrides
});

describe('EmailJobListComponent', () => {
  let component: EmailJobListComponent;
  let emailJobServiceSpy: jasmine.SpyObj<EmailJobService>;
  let notificationsSpy: jasmine.SpyObj<EmailJobNotificationsService>;
  let routerSpy: jasmine.SpyObj<Router>;
  let locationSpy: jasmine.SpyObj<Location>;
  let progressSubject: Subject<EmailJobProgress>;
  let completedSubject: Subject<EmailJobCompleted>;

  beforeEach(() => {
    progressSubject = new Subject<EmailJobProgress>();
    completedSubject = new Subject<EmailJobCompleted>();

    emailJobServiceSpy = jasmine.createSpyObj('EmailJobService', ['getRecentJobs']);
    notificationsSpy = jasmine.createSpyObj('EmailJobNotificationsService', ['connect', 'disconnect']);
    Object.defineProperty(notificationsSpy, 'progress$', { get: () => progressSubject.asObservable() });
    Object.defineProperty(notificationsSpy, 'completed$', { get: () => completedSubject.asObservable() });
    routerSpy = jasmine.createSpyObj('Router', ['navigate', 'parseUrl']);
    locationSpy = jasmine.createSpyObj('Location', ['replaceState']);
    routerSpy.parseUrl.and.returnValue({ queryParams: {}, fragment: null } as any);
    Object.defineProperty(routerSpy, 'url', { get: () => '/manage/send-email' });

    emailJobServiceSpy.getRecentJobs.and.returnValue(of([]));

    TestBed.configureTestingModule({
      providers: [
        EmailJobListComponent,
        { provide: EmailJobService, useValue: emailJobServiceSpy },
        { provide: EmailJobNotificationsService, useValue: notificationsSpy },
        { provide: Router, useValue: routerSpy },
        { provide: Location, useValue: locationSpy }
      ]
    });

    component = TestBed.inject(EmailJobListComponent);
  });

  it('fetches jobs and connects to notifications on init', () => {
    component.ngOnInit();
    expect(emailJobServiceSpy.getRecentJobs).toHaveBeenCalledTimes(1);
    expect(notificationsSpy.connect).toHaveBeenCalledTimes(1);
  });

  it('populates jobs list from service', () => {
    const jobs = [makeJob('j1'), makeJob('j2')];
    emailJobServiceSpy.getRecentJobs.and.returnValue(of(jobs));
    component.ngOnInit();
    expect(component.jobs).toEqual(jobs);
    expect(component.loading).toBeFalse();
  });

  it('sets errorText and clears loading on fetch failure', () => {
    emailJobServiceSpy.getRecentJobs.and.returnValue(throwError(() => ({ status: 500 })));
    component.ngOnInit();
    expect(component.loading).toBeFalse();
    expect(component.errorText).toBeTruthy();
  });

  it('loadJobs() re-fetches the list', () => {
    component.ngOnInit();
    emailJobServiceSpy.getRecentJobs.calls.reset();
    component.loadJobs();
    expect(emailJobServiceSpy.getRecentJobs).toHaveBeenCalledTimes(1);
  });

  it('updates an existing job in the list on a progress event', () => {
    const job = makeJob('j1', { status: 'Queued', sentCount: 0 });
    emailJobServiceSpy.getRecentJobs.and.returnValue(of([job]));
    component.ngOnInit();

    progressSubject.next({ jobId: 'j1', status: 'InProgress', sentCount: 5, failedCount: 0, totalRecipients: 10 });

    expect(component.jobs[0].status).toBe('InProgress');
    expect(component.jobs[0].sentCount).toBe(5);
  });

  it('ignores progress events for unknown job ids', () => {
    emailJobServiceSpy.getRecentJobs.and.returnValue(of([makeJob('j1')]));
    component.ngOnInit();

    progressSubject.next({ jobId: 'unknown', status: 'InProgress', sentCount: 3, failedCount: 0, totalRecipients: 10 });

    expect(component.jobs.length).toBe(1);
    expect(component.jobs[0].id).toBe('j1');
  });

  it('updates an existing job in the list on a completed event', () => {
    const job = makeJob('j1', { status: 'InProgress', sentCount: 8 });
    emailJobServiceSpy.getRecentJobs.and.returnValue(of([job]));
    component.ngOnInit();

    completedSubject.next({ jobId: 'j1', status: 'Completed', sentCount: 10, failedCount: 0, totalRecipients: 10, lastError: null });

    expect(component.jobs[0].status).toBe('Completed');
    expect(component.jobs[0].sentCount).toBe(10);
  });

  it('disconnects and unsubscribes on destroy', () => {
    component.ngOnInit();
    component.ngOnDestroy();
    expect(notificationsSpy.disconnect).toHaveBeenCalledTimes(1);
  });

  it('navigates to job detail on viewJob()', () => {
    component.viewJob(makeJob('j1'));
    expect(routerSpy.navigate).toHaveBeenCalledWith(['/manage/email-jobs', 'j1']);
  });
});
