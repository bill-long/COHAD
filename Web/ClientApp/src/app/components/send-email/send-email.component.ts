import { DOCUMENT } from '@angular/common';
import { HttpClient, HttpResponse } from '@angular/common/http';
import { AfterViewInit, Component, Inject, OnDestroy, OnInit, ViewChild } from '@angular/core';
import { ActivatedRoute, NavigationEnd, Router } from '@angular/router';
import Quill from 'quill';
import { Observable, Subscription, zip } from 'rxjs';
import { filter, map, take } from 'rxjs/operators';
import { EmailJobSummary, EmailJobStatus } from 'src/app/models';
import { EmailJobListComponent } from 'src/app/components/email-job-list/email-job-list.component';
import { rolePermissions } from 'src/app/services/rolepermission.service';
import { ApplicationInsightsService } from 'src/app/services/application-insights.service';
import { EmailJobNotificationsService } from 'src/app/services/email-job-notifications.service';
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
  testSucceeded = false;
  sendSucceeded = false;
  errorText!: string | null;

  // Email job queue state
  activeJob: EmailJobSummary | null = null;
  jobCompleted = false;

  private jobSubscriptions: Subscription[] = [];

  constructor(
    private httpClient: HttpClient,
    private route: ActivatedRoute,
    private router: Router,
    private telemetry: ApplicationInsightsService,
    private emailJobNotifications: EmailJobNotificationsService,
    @Inject(applicationState) private appState: Observable<ApplicationState>,
    @Inject(DOCUMENT) private document: Document,
  ) {
    const Block = Quill.import('blots/block') as any;
    class MyBlock extends Block {
      static tagName = 'DIV';
    }
    Quill.register(MyBlock, true);
    zip(
      this.canSendFromBoard,
      this.canSendFromGardenClub,
      this.canSendFromSocialCommittee,
      this.canSendFromWelcomeCommittee,
      this.canSendFromSunshineCommittee,
    )
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

  get apiUserEmail(): Observable<string> {
    return this.appState.pipe(map(s => s.apiUser?.email || ''));
  }

  get jobProgressPercent(): number {
    if (!this.activeJob || this.activeJob.totalRecipients === 0) return 0;
    const processed = this.activeJob.sentCount + this.activeJob.failedCount;
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

  sendEmail(isTest: boolean) {
    this.editEnabled = false;
    if (isTest) {
      this.testSucceeded = false;
    }

    this.httpClient
      .put(
        `api/email/${this.senderEndpoint}`,
        {
          subject: this.subject,
          htmlBody: this.htmlBody,
          isTestEmail: isTest,
        },
        { observe: 'response' },
      )
      .subscribe({
        next: (resp: HttpResponse<object>) => {
          this.errorText = null;
          if (isTest) {
            // Test email: synchronous 200 OK
            this.testSucceeded = true;
            this.editEnabled = true;
          } else if (resp.status === 202) {
            // Non-test: 202 Accepted with EmailJobSummary
            const jobSummary = resp.body as EmailJobSummary;
            this.telemetry.trackEvent('EmailSent', { sender: this.senderEndpoint, jobId: jobSummary.id });
            this.activeJob = jobSummary;
            this.jobCompleted = false;
            this.sendSucceeded = true;
            this.subscribeToJobUpdates(jobSummary.id);
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
    this.testSucceeded = false;
    this.activeJob = null;
    this.jobCompleted = false;
    this.teardownJobSubscriptions();
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
            totalRecipients: event.totalRecipients,
            lastError: event.lastError,
          };
          this.jobCompleted = true;
        }
      }),
    );
  }

  private teardownJobSubscriptions(): void {
    this.jobSubscriptions.forEach(s => s.unsubscribe());
    this.jobSubscriptions = [];
  }
}
