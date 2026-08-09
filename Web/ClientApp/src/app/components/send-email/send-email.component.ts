import { DOCUMENT } from '@angular/common';
import { HttpClient, HttpResponse } from '@angular/common/http';
import { AfterViewInit, Component, Inject, OnDestroy, OnInit, ViewChild } from '@angular/core';
import { ActivatedRoute, NavigationEnd, Router } from '@angular/router';
import { Observable, Subscription, combineLatest, zip } from 'rxjs';
import { filter, map, take } from 'rxjs/operators';
import { EmailJobSummary, EmailJobStatus, TestRecipientOption } from 'src/app/models';
import { EmailJobListComponent } from 'src/app/components/email-job-list/email-job-list.component';
import { rolePermissions } from 'src/app/services/rolepermission.service';
import { ApplicationInsightsService } from 'src/app/services/application-insights.service';
import { EmailJobNotificationsService } from 'src/app/services/email-job-notifications.service';
import { EmailJobService } from 'src/app/services/email-job.service';
import { applicationState, ApplicationState } from 'src/app/state';
import { httpErrorMessage } from 'src/app/utils/http-error-message';
import { EMAIL_JOBS_FOCUS_JOB_QUERY_PARAM, EMAIL_JOBS_SECTION_ANCHOR } from 'src/app/constants/email-jobs-send-page.constants';

@Component({
  selector: 'app-send-email',
  templateUrl: './send-email.component.html',
  styleUrls: ['./send-email.component.css'],
  standalone: false,
})
export class SendEmailComponent implements OnInit, AfterViewInit, OnDestroy {
  @ViewChild(EmailJobListComponent) emailJobList?: EmailJobListComponent;

  /** Set when returning from job detail; list scrolls this row after jobs load. */
  focusJobId: string | null = null;

  /** Bound to the jobs section wrapper so fragment / getElementById stay aligned with `EMAIL_JOBS_SECTION_ANCHOR`. */
  readonly emailJobsSectionAnchor = EMAIL_JOBS_SECTION_ANCHOR;

  private routeQuerySub?: Subscription;
  private routerEventsSub?: Subscription;
  private jobsSectionScrollTimeouts: ReturnType<typeof setTimeout>[] = [];

  senderEndpoint!: string;
  subject!: string;
  htmlBody!: string;
  editEnabled = true;
  sendSucceeded = false;
  errorText!: string | null;

  // Test recipient picker
  testRecipients: TestRecipientOption[] = [];
  selectedTestRecipients: Set<string> = new Set();

  // Email job queue state (real send)
  activeJob: EmailJobSummary | null = null;
  jobCompleted = false;

  // Test send job (separate so the form stays intact)
  activeTestJob: EmailJobSummary | null = null;
  testJobCompleted = false;

  private jobSubscriptions: Subscription[] = [];
  private testJobSubscriptions: Subscription[] = [];

  /**
   * The five committees that can be sent as, in the order the default-selection logic prefers them.
   * Declared once so the compose card's visibility and that default cannot disagree about who can
   * send; the template's option list is the third reader of the same rule.
   */
  private get senderAvailability(): Observable<boolean>[] {
    return [
      this.canSendFromBoard,
      this.canSendFromGardenClub,
      this.canSendFromSocialCommittee,
      this.canSendFromWelcomeCommittee,
      this.canSendFromSunshineCommittee,
    ];
  }

  constructor(
    private httpClient: HttpClient,
    private route: ActivatedRoute,
    private router: Router,
    private telemetry: ApplicationInsightsService,
    private emailJobNotifications: EmailJobNotificationsService,
    private emailJobService: EmailJobService,
    @Inject(applicationState) private appState: Observable<ApplicationState>,
    @Inject(DOCUMENT) private document: Document,
  ) {
    zip(...this.senderAvailability)
      .pipe(take(1))
      .subscribe(([board, garden, social, welcome, sunshine]) => {
        if (board) {
          this.senderEndpoint = 'from-board';
        } else if (garden) {
          this.senderEndpoint = 'from-garden';
        } else if (social) {
          this.senderEndpoint = 'from-social';
        } else if (welcome) {
          this.senderEndpoint = 'from-welcome';
        } else if (sunshine) {
          this.senderEndpoint = 'from-sunshine';
        }
      });
  }

  ngOnInit(): void {
    this.routeQuerySub = this.route.queryParamMap
      .pipe(map(q => q.get(EMAIL_JOBS_FOCUS_JOB_QUERY_PARAM)?.toLowerCase() ?? null))
      .subscribe(id => {
        this.focusJobId = id;
      });

    this.emailJobService.getTestRecipients().subscribe({
      next: recipients => {
        this.testRecipients = recipients;
      },
      error: () => {
        this.testRecipients = [];
      },
    });
  }

  ngAfterViewInit(): void {
    this.routerEventsSub = this.router.events.pipe(filter((e): e is NavigationEnd => e instanceof NavigationEnd)).subscribe(() => {
      if (this.router.parseUrl(this.router.url).fragment !== EMAIL_JOBS_SECTION_ANCHOR) {
        return;
      }
      // Run after RouterScroller (setTimeout + rAF) so this wins over scroll-to-top.
      this.scheduleScrollJobsSectionAfterNav();
    });
  }

  ngOnDestroy(): void {
    this.clearJobsSectionScrollTimeouts();
    this.routeQuerySub?.unsubscribe();
    this.routerEventsSub?.unsubscribe();
    this.teardownJobSubscriptions();
    this.teardownTestJobSubscriptions();
  }

  /** Ensures the jobs block is in view when landing with #email-jobs (runs after RouterScroller). */
  private scheduleScrollJobsSectionAfterNav(): void {
    this.clearJobsSectionScrollTimeouts();
    const delaysMs = [0, 32, 100, 250, 400];
    for (const ms of delaysMs) {
      const id = setTimeout(() => this.scrollJobsSectionIntoViewIfNeeded(), ms);
      this.jobsSectionScrollTimeouts.push(id);
    }
  }

  private clearJobsSectionScrollTimeouts(): void {
    for (const id of this.jobsSectionScrollTimeouts) {
      clearTimeout(id);
    }
    this.jobsSectionScrollTimeouts = [];
  }

  private scrollJobsSectionIntoViewIfNeeded(): void {
    const el = this.document.getElementById(EMAIL_JOBS_SECTION_ANCHOR);
    if (!el) {
      return;
    }
    const rect = el.getBoundingClientRect();
    // Jobs block already near top of viewport (below sticky nav): skip.
    if (rect.top <= 100 && rect.top >= -40) {
      return;
    }
    // Instant so we reliably beat any remaining scroll-to-top and avoid fighting smooth row scroll.
    el.scrollIntoView({ behavior: 'auto', block: 'start' });
  }

  /**
   * True when the caller can send as at least one committee.
   * <para>
   * This is not the same as being allowed on this page. Managing email jobs is one permission and
   * sending is another: Architectural and Landscape have no mailbox to send as, and neither does an
   * Administrator who holds no committee role - the per-committee policies admit only the committee's
   * own role. All of them get the job list; none of them gets a compose form that could only fail.
   * </para>
   */
  readonly canSendFromAny: Observable<boolean> = combineLatest(this.senderAvailability).pipe(
    map(flags => flags.some(Boolean)),
  );

  get canSendFromBoard(): Observable<boolean> {
    return this.appState.pipe(
      map(s => s.apiUser != null && s.apiUser.roles.filter(r => rolePermissions.sendEmailAsBoard.includes(r)).length > 0),
    );
  }

  get canSendFromWelcomeCommittee(): Observable<boolean> {
    return this.appState.pipe(
      map(s => s.apiUser != null && s.apiUser.roles.filter(r => rolePermissions.sendEmailAsWelcomeCommittee.includes(r)).length > 0),
    );
  }

  get canSendFromGardenClub(): Observable<boolean> {
    return this.appState.pipe(
      map(s => s.apiUser != null && s.apiUser.roles.filter(r => rolePermissions.sendEmailAsGardenClub.includes(r)).length > 0),
    );
  }

  get canSendFromSocialCommittee(): Observable<boolean> {
    return this.appState.pipe(
      map(s => s.apiUser != null && s.apiUser.roles.filter(r => rolePermissions.sendEmailAsSocialCommittee.includes(r)).length > 0),
    );
  }

  get canSendFromSunshineCommittee(): Observable<boolean> {
    return this.appState.pipe(
      map(s => s.apiUser != null && s.apiUser.roles.filter(r => rolePermissions.sendEmailAsSunshineCommittee.includes(r)).length > 0),
    );
  }

  get jobProgressPercent(): number {
    if (!this.activeJob || this.activeJob.totalRecipients === 0) return 0;
    // Suppressed recipients are handled (skipped deliberately), so they count as processed - an
    // all-suppressed Completed job reads 100%, not a permanently-stalled 0%.
    const processed = this.activeJob.sentCount + this.activeJob.failedCount + this.activeJob.suppressedCount;
    return Math.round((processed / this.activeJob.totalRecipients) * 100);
  }

  jobStatusLabel(status: EmailJobStatus): string {
    switch (status) {
      case 'InProgress':
        return 'In Progress';
      case 'PartiallyCompleted':
        return 'Partially Completed';
      default:
        return status;
    }
  }

  toggleTestRecipient(email: string): void {
    if (this.selectedTestRecipients.has(email)) {
      this.selectedTestRecipients.delete(email);
    } else {
      this.selectedTestRecipients.add(email);
    }
    // Trigger change detection by replacing the Set reference
    this.selectedTestRecipients = new Set(this.selectedTestRecipients);
  }

  get hasSelectedTestRecipients(): boolean {
    return this.selectedTestRecipients.size > 0;
  }

  sendEmail(isTest: boolean) {
    this.editEnabled = false;
    this.errorText = null;

    if (isTest) {
      this.activeTestJob = null;
      this.testJobCompleted = false;
      this.teardownTestJobSubscriptions();
    }

    const payload: Record<string, unknown> = {
      subject: this.subject,
      htmlBody: this.htmlBody,
      isTestEmail: isTest,
    };
    if (isTest) {
      payload['testRecipientEmails'] = Array.from(this.selectedTestRecipients);
    }

    this.httpClient.put(`api/email/${this.senderEndpoint}`, payload, { observe: 'response' }).subscribe({
      next: (resp: HttpResponse<object>) => {
        if (resp.status === 202) {
          const jobSummary = resp.body as EmailJobSummary;
          this.telemetry.trackEvent('EmailSent', {
            sender: this.senderEndpoint,
            jobId: jobSummary.id,
            isTest: String(isTest),
          });
          if (isTest) {
            this.activeTestJob = jobSummary;
            this.testJobCompleted = false;
            this.subscribeToTestJobUpdates(jobSummary.id);
            this.editEnabled = true;
          } else {
            this.activeJob = jobSummary;
            this.jobCompleted = false;
            this.sendSucceeded = true;
            this.subscribeToJobUpdates(jobSummary.id);
          }
          this.emailJobList?.loadJobs();
        } else if (resp.status === 200 && resp.body && (resp.body as any).message) {
          // 200 OK with a message means no matching recipients
          this.errorText = (resp.body as any).message;
          this.editEnabled = true;
        } else {
          // Fallback for unexpected success responses
          this.telemetry.trackEvent('EmailSent', { sender: this.senderEndpoint });
          this.sendSucceeded = true;
          this.editEnabled = true;
        }
      },
      error: err => {
        this.errorText = httpErrorMessage(err, 'Failed to send email.');
        this.editEnabled = true;
      },
    });
  }

  sendNew() {
    this.subject = '';
    this.htmlBody = '';
    this.editEnabled = true;
    this.sendSucceeded = false;
    this.activeJob = null;
    this.jobCompleted = false;
    this.activeTestJob = null;
    this.testJobCompleted = false;
    this.selectedTestRecipients = new Set();
    this.teardownJobSubscriptions();
    this.teardownTestJobSubscriptions();
  }

  private subscribeToJobUpdates(jobId: string): void {
    this.teardownJobSubscriptions();

    this.jobSubscriptions.push(
      this.emailJobNotifications.progress$.subscribe(event => {
        if (event.jobId === jobId && this.activeJob) {
          this.activeJob = {
            ...this.activeJob,
            status: event.status,
            sentCount: event.sentCount,
            failedCount: event.failedCount,
            suppressedCount: event.suppressedCount,
            totalRecipients: event.totalRecipients,
          };
        }
      }),
      this.emailJobNotifications.completed$.subscribe(event => {
        if (event.jobId === jobId && this.activeJob) {
          this.activeJob = {
            ...this.activeJob,
            status: event.status,
            sentCount: event.sentCount,
            failedCount: event.failedCount,
            suppressedCount: event.suppressedCount,
            totalRecipients: event.totalRecipients,
            lastError: event.lastError,
          };
          this.jobCompleted = true;
        }
      }),
    );
  }

  private subscribeToTestJobUpdates(jobId: string): void {
    this.teardownTestJobSubscriptions();

    this.testJobSubscriptions.push(
      this.emailJobNotifications.progress$.subscribe(event => {
        if (event.jobId === jobId && this.activeTestJob) {
          this.activeTestJob = {
            ...this.activeTestJob,
            status: event.status,
            sentCount: event.sentCount,
            failedCount: event.failedCount,
            suppressedCount: event.suppressedCount,
            totalRecipients: event.totalRecipients,
          };
        }
      }),
      this.emailJobNotifications.completed$.subscribe(event => {
        if (event.jobId === jobId && this.activeTestJob) {
          this.activeTestJob = {
            ...this.activeTestJob,
            status: event.status,
            sentCount: event.sentCount,
            failedCount: event.failedCount,
            suppressedCount: event.suppressedCount,
            totalRecipients: event.totalRecipients,
            lastError: event.lastError,
          };
          this.testJobCompleted = true;
        }
      }),
    );
  }

  private teardownJobSubscriptions(): void {
    this.jobSubscriptions.forEach(s => s.unsubscribe());
    this.jobSubscriptions = [];
  }

  private teardownTestJobSubscriptions(): void {
    this.testJobSubscriptions.forEach(s => s.unsubscribe());
    this.testJobSubscriptions = [];
  }
}
