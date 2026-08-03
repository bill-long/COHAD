import { BreakpointObserver, BreakpointState } from '@angular/cdk/layout';
import { CommonModule, Location } from '@angular/common';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { MatCardModule } from '@angular/material/card';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { Router } from '@angular/router';
import { BehaviorSubject, Subject, of, throwError } from 'rxjs';
import { EmailJobListComponent } from './email-job-list.component';
import { EmailJobService } from 'src/app/services/email-job.service';
import { EmailJobNotificationsService } from 'src/app/services/email-job-notifications.service';
import { EmailJobSummary, EmailJobProgress, EmailJobCompleted } from 'src/app/models';
import { EMAIL_JOBS_FOCUS_JOB_QUERY_PARAM, EMAIL_JOBS_SECTION_ANCHOR } from 'src/app/constants/email-jobs-send-page.constants';

const makeJob = (id: string, overrides: Partial<EmailJobSummary> = {}): EmailJobSummary => ({
  id,
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
  ...overrides,
});

describe('EmailJobListComponent', () => {
  let component: EmailJobListComponent;
  let emailJobServiceSpy: jasmine.SpyObj<EmailJobService>;
  let notificationsSpy: jasmine.SpyObj<EmailJobNotificationsService>;
  let routerSpy: jasmine.SpyObj<Router>;
  let locationSpy: jasmine.SpyObj<Location>;
  /** Mutable current URL for `router.url` (tests may assign `.value`). */
  let routerUrlMock: { value: string };
  let progressSubject: Subject<EmailJobProgress>;
  let completedSubject: Subject<EmailJobCompleted>;
  let breakpointState: BehaviorSubject<BreakpointState>;

  beforeEach(() => {
    progressSubject = new Subject<EmailJobProgress>();
    completedSubject = new Subject<EmailJobCompleted>();
    breakpointState = new BehaviorSubject<BreakpointState>({ matches: false, breakpoints: {} });

    emailJobServiceSpy = jasmine.createSpyObj('EmailJobService', ['getRecentJobs']);
    notificationsSpy = jasmine.createSpyObj('EmailJobNotificationsService', ['connect', 'disconnect']);
    Object.defineProperty(notificationsSpy, 'progress$', { get: () => progressSubject.asObservable() });
    Object.defineProperty(notificationsSpy, 'completed$', { get: () => completedSubject.asObservable() });
    routerSpy = jasmine.createSpyObj('Router', ['navigate', 'parseUrl', 'serializeUrl']);
    locationSpy = jasmine.createSpyObj('Location', ['replaceState']);
    routerSpy.parseUrl.and.returnValue({ queryParams: {}, fragment: null } as any);
    routerSpy.serializeUrl.and.returnValue('/manage/send-email');
    routerUrlMock = { value: '/manage/send-email' };
    Object.defineProperty(routerSpy, 'url', { get: () => routerUrlMock.value });

    emailJobServiceSpy.getRecentJobs.and.returnValue(of([]));

    TestBed.configureTestingModule({
      providers: [
        EmailJobListComponent,
        { provide: EmailJobService, useValue: emailJobServiceSpy },
        { provide: EmailJobNotificationsService, useValue: notificationsSpy },
        { provide: Router, useValue: routerSpy },
        { provide: Location, useValue: locationSpy },
        { provide: BreakpointObserver, useValue: { observe: () => breakpointState.asObservable() } },
      ],
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

  it('renders the table on desktop and the stacked list when the viewport is narrow', () => {
    // Rendered, not just observed: an *ngIf removed from the template would otherwise leave phones
    // with the desktop table while a subscription-only assertion still passed.
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      declarations: [EmailJobListComponent],
      imports: [CommonModule, MatCardModule, MatProgressSpinnerModule],
      providers: [
        { provide: EmailJobService, useValue: emailJobServiceSpy },
        { provide: EmailJobNotificationsService, useValue: notificationsSpy },
        { provide: Router, useValue: routerSpy },
        { provide: Location, useValue: locationSpy },
        { provide: BreakpointObserver, useValue: { observe: () => breakpointState.asObservable() } },
      ],
    });
    emailJobServiceSpy.getRecentJobs.and.returnValue(of([makeJob('j1')]));

    const fixture: ComponentFixture<EmailJobListComponent> = TestBed.createComponent(EmailJobListComponent);
    fixture.detectChanges();

    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('.jobs-table')).not.toBeNull();
    expect(el.querySelector('.job-entry-list')).toBeNull();

    breakpointState.next({ matches: true, breakpoints: {} });
    fixture.detectChanges();

    expect(el.querySelector('.job-entry-list')).not.toBeNull();
    expect(el.querySelector('.jobs-table')).toBeNull();
    // Every field a row is identified by survives the switch, rather than being hidden.
    const entry = el.querySelector('.job-entry') as HTMLElement;
    expect(entry.textContent).toContain('Test subject');
    expect(entry.textContent).toContain('COHAD Board');
    expect(entry.textContent).toContain('Board opt-in residents');
    // The li keeps its listitem role; the clickable element sits inside it.
    expect(entry.getAttribute('role')).toBeNull();
    expect(entry.querySelector('[role="button"]')).not.toBeNull();
    expect(el.querySelector('.job-entry-list')!.getAttribute('role')).toBe('list');

    fixture.destroy();
  });

  it('partiesFor() names the resident, not the committee, on a forwarded job', () => {
    const job = makeJob('j1', {
      category: 'committee-forward',
      fromEmail: 'architectural@cohad.org',
      fromDisplay: 'Architectural Committee',
      toDisplay: 'Architectural Committee forwarding members',
      originalSenderEmail: 'jane@example.com',
      originalSenderDisplay: 'Jane Doe',
    });

    const parties = component.partiesFor(job);

    expect(parties.fromShort).toBe('Jane Doe');
    // The list's To column shows who the message was addressed to, the same as the detail page's To row.
    expect(parties.toShort).toBe('Architectural Committee');
  });

  it('partiesFor() recomputes after a job is replaced by a progress update', () => {
    const job = makeJob('j1', { toDisplay: null, totalRecipients: 10 });
    emailJobServiceSpy.getRecentJobs.and.returnValue(of([job]));
    component.ngOnInit();
    expect(component.partiesFor(component.jobs[0]).to).toBe('10 recipients');

    progressSubject.next({ jobId: 'j1', status: 'InProgress', sentCount: 1, failedCount: 0, totalRecipients: 12 });

    expect(component.partiesFor(component.jobs[0]).to).toBe('12 recipients');
  });

  it('stripFocusNavigationFromUrl calls Location.replaceState when focusJob or hash present', () => {
    const parsed = {
      queryParams: { [EMAIL_JOBS_FOCUS_JOB_QUERY_PARAM]: 'j1', keep: '2' } as Record<string, string>,
      fragment: EMAIL_JOBS_SECTION_ANCHOR as string | null,
    };
    routerSpy.parseUrl.and.returnValue(parsed as any);
    routerSpy.serializeUrl.and.callFake((tree: typeof parsed) => {
      const q = Object.keys(tree.queryParams)
        .map(k => `${encodeURIComponent(k)}=${encodeURIComponent(tree.queryParams[k])}`)
        .join('&');
      const base = '/manage/send-email' + (q ? '?' + q : '');
      return tree.fragment ? `${base}#${tree.fragment}` : base;
    });
    routerUrlMock.value = `/manage/send-email?focusJob=j1&keep=2#${EMAIL_JOBS_SECTION_ANCHOR}`;
    (component as unknown as { stripFocusNavigationFromUrl(): void }).stripFocusNavigationFromUrl();
    expect(locationSpy.replaceState).toHaveBeenCalledWith('/manage/send-email', 'keep=2');
    expect(parsed.queryParams[EMAIL_JOBS_FOCUS_JOB_QUERY_PARAM]).toBeUndefined();
    expect(parsed.fragment).toBeNull();
  });

  it('stripFocusNavigationFromUrl is a no-op when URL has no focusJob or email-jobs fragment', () => {
    locationSpy.replaceState.calls.reset();
    routerSpy.parseUrl.and.returnValue({ queryParams: { other: '1' }, fragment: null } as any);
    routerUrlMock.value = '/manage/send-email?other=1';
    (component as unknown as { stripFocusNavigationFromUrl(): void }).stripFocusNavigationFromUrl();
    expect(locationSpy.replaceState).not.toHaveBeenCalled();
  });

  it('stripFocusNavigationFromUrl strips focusJob even when the query value is empty', () => {
    const parsed = {
      queryParams: { [EMAIL_JOBS_FOCUS_JOB_QUERY_PARAM]: '', keep: '1' } as Record<string, string>,
      fragment: null as string | null,
    };
    routerSpy.parseUrl.and.returnValue(parsed as any);
    routerSpy.serializeUrl.and.callFake((tree: typeof parsed) => {
      const q = Object.keys(tree.queryParams)
        .map(k => `${encodeURIComponent(k)}=${encodeURIComponent(tree.queryParams[k])}`)
        .join('&');
      return '/manage/send-email' + (q ? '?' + q : '');
    });
    routerUrlMock.value = '/manage/send-email?focusJob=&keep=1';
    (component as unknown as { stripFocusNavigationFromUrl(): void }).stripFocusNavigationFromUrl();
    expect(locationSpy.replaceState).toHaveBeenCalledWith('/manage/send-email', 'keep=1');
    expect(parsed.queryParams[EMAIL_JOBS_FOCUS_JOB_QUERY_PARAM]).toBeUndefined();
  });

  it('stripFocusNavigationFromUrl preserves a non–email-jobs fragment when stripping focusJob', () => {
    const parsed = {
      queryParams: { [EMAIL_JOBS_FOCUS_JOB_QUERY_PARAM]: 'j1' } as Record<string, string>,
      fragment: 'other-section' as string | null,
    };
    routerSpy.parseUrl.and.returnValue(parsed as any);
    routerSpy.serializeUrl.and.callFake((tree: typeof parsed) => {
      const q = Object.keys(tree.queryParams)
        .map(k => `${encodeURIComponent(k)}=${encodeURIComponent(tree.queryParams[k])}`)
        .join('&');
      const base = '/manage/send-email' + (q ? '?' + q : '');
      return tree.fragment ? `${base}#${tree.fragment}` : base;
    });
    routerUrlMock.value = '/manage/send-email?focusJob=j1#other-section';
    (component as unknown as { stripFocusNavigationFromUrl(): void }).stripFocusNavigationFromUrl();
    expect(locationSpy.replaceState).toHaveBeenCalledWith('/manage/send-email#other-section', '');
    expect(parsed.fragment).toBe('other-section');
  });
});
