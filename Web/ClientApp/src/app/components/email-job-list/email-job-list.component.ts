import { DOCUMENT, Location } from '@angular/common';
import { Component, Inject, Input, OnChanges, OnDestroy, OnInit, SimpleChanges } from '@angular/core';
import { Router } from '@angular/router';
import { Subscription } from 'rxjs';
import { EmailJobSummary, EmailJobStatus } from 'src/app/models';
import { EmailJobService } from 'src/app/services/email-job.service';
import { EmailJobNotificationsService } from 'src/app/services/email-job-notifications.service';
import { httpErrorMessage } from 'src/app/utils/http-error-message';
import {
  EMAIL_JOBS_FOCUS_JOB_QUERY_PARAM,
  EMAIL_JOBS_SECTION_ANCHOR,
} from 'src/app/constants/email-jobs-send-page.constants';

@Component({
    selector: 'app-email-job-list',
    templateUrl: './email-job-list.component.html',
    styleUrls: ['./email-job-list.component.css'],
    standalone: false
})
export class EmailJobListComponent implements OnInit, OnChanges, OnDestroy {
  /** When set (e.g. after navigating back from job detail), scroll this row into view once data is ready. */
  @Input() scrollTargetJobId: string | null = null;

  jobs: EmailJobSummary[] = [];
  loading = true;
  errorText: string | null = null;

  /** After a successful scroll-to-row, skip repeating when `loadJobs()` runs again (e.g. after sending mail) while `focusJob` stays in the URL. */
  private scrolledToTargetJobId: string | null = null;

  private subscriptions: Subscription[] = [];
  private targetRowScrollTimeouts: ReturnType<typeof setTimeout>[] = [];
  private destroyed = false;

  constructor(
    private emailJobService: EmailJobService,
    private emailJobNotifications: EmailJobNotificationsService,
    private router: Router,
    private location: Location,
    @Inject(DOCUMENT) private document: Document
  ) { }

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['scrollTargetJobId']) {
      const prev = changes['scrollTargetJobId'].previousValue as string | null | undefined;
      const cur = changes['scrollTargetJobId'].currentValue as string | null | undefined;
      const prevNorm = prev?.toLowerCase() ?? null;
      const curNorm = cur?.toLowerCase() ?? null;
      if (prevNorm !== curNorm) {
        this.scrolledToTargetJobId = null;
      }
      this.scheduleScrollToTargetJob();
    }
  }

  ngOnInit(): void {
    this.loadJobs();
    this.emailJobNotifications.connect();

    this.subscriptions.push(
      this.emailJobNotifications.progress$.subscribe(event => {
        this.updateJobInList(event.jobId, {
          status: event.status,
          sentCount: event.sentCount,
          failedCount: event.failedCount,
          totalRecipients: event.totalRecipients
        });
      }),
      this.emailJobNotifications.completed$.subscribe(event => {
        this.updateJobInList(event.jobId, {
          status: event.status,
          sentCount: event.sentCount,
          failedCount: event.failedCount,
          totalRecipients: event.totalRecipients,
          lastError: event.lastError
        });
      })
    );
  }

  ngOnDestroy(): void {
    this.destroyed = true;
    this.clearTargetRowScrollScheduling();
    this.subscriptions.forEach(s => s.unsubscribe());
    this.emailJobNotifications.disconnect();
  }

  loadJobs(): void {
    this.loading = true;
    this.errorText = null;
    this.emailJobService.getRecentJobs().subscribe({
      next: jobs => {
        this.jobs = jobs;
        this.loading = false;
        this.scheduleScrollToTargetJob();
      },
      error: err => {
        this.errorText = httpErrorMessage(err, 'Failed to load email jobs.');
        this.loading = false;
      }
    });
  }

  viewJob(job: EmailJobSummary): void {
    this.router.navigate(['/manage/email-jobs', job.id]);
  }

  statusLabel(status: EmailJobStatus): string {
    switch (status) {
      case 'InProgress': return 'In Progress';
      case 'PartiallyCompleted': return 'Partial';
      default: return status;
    }
  }

  statusClass(status: EmailJobStatus): string {
    switch (status) {
      case 'Queued': return 'status-queued';
      case 'InProgress': return 'status-in-progress';
      case 'Completed': return 'status-completed';
      case 'PartiallyCompleted': return 'status-partial';
      case 'Failed': return 'status-failed';
      case 'Cancelled': return 'status-cancelled';
      default: return '';
    }
  }

  private clearTargetRowScrollScheduling(): void {
    for (const t of this.targetRowScrollTimeouts) {
      clearTimeout(t);
    }
    this.targetRowScrollTimeouts = [];
  }

  private targetRowScrollBehavior(): ScrollBehavior {
    return this.document.defaultView?.matchMedia('(prefers-reduced-motion: reduce)').matches
      ? 'auto'
      : 'smooth';
  }

  private scheduleScrollToTargetJob(): void {
    const id = this.scrollTargetJobId?.toLowerCase();
    if (!id || this.loading) {
      return;
    }
    if (this.scrolledToTargetJobId === id) {
      return;
    }
    this.clearTargetRowScrollScheduling();
    const rowId = `email-job-row-${id}`;
    let scrolled = false;
    const tryOnce = (): void => {
      if (this.destroyed || scrolled) {
        return;
      }
      const el = this.document.getElementById(rowId);
      if (el) {
        scrolled = true;
        this.scrolledToTargetJobId = id;
        el.scrollIntoView({ behavior: this.targetRowScrollBehavior(), block: 'start' });
        this.clearTargetRowScrollScheduling();
        this.stripFocusNavigationFromUrl();
      }
    };
    requestAnimationFrame(() => requestAnimationFrame(tryOnce));
    const delaysMs = [16, 32, 64, 100, 200, 400, 700];
    for (const ms of delaysMs) {
      this.targetRowScrollTimeouts.push(setTimeout(tryOnce, ms));
    }
  }

  jobRowDomId(job: EmailJobSummary): string {
    return `email-job-row-${job.id.toLowerCase()}`;
  }

  /**
   * Drops `focusJob` from the query string and removes the `#email-jobs` fragment (see `EMAIL_JOBS_SECTION_ANCHOR`)
   * once the target row has been scrolled into view. Other query params and non–email-jobs fragments are left unchanged.
   */
  private stripFocusNavigationFromUrl(): void {
    const parsed = this.router.parseUrl(this.router.url);
    const hasFocusJob = !!parsed.queryParams[EMAIL_JOBS_FOCUS_JOB_QUERY_PARAM];
    const hasEmailJobsFragment = parsed.fragment === EMAIL_JOBS_SECTION_ANCHOR;
    if (!hasFocusJob && !hasEmailJobsFragment) {
      return;
    }
    const queryParams = { ...parsed.queryParams };
    if (hasFocusJob) {
      delete queryParams[EMAIL_JOBS_FOCUS_JOB_QUERY_PARAM];
    }
    parsed.queryParams = queryParams;
    if (hasEmailJobsFragment) {
      parsed.fragment = null;
    }
    const serialized = this.router.serializeUrl(parsed);
    const qIndex = serialized.indexOf('?');
    // Use Location.replaceState so we do not run a Router navigation (scrollPositionRestoration would jump to top).
    if (qIndex >= 0) {
      this.location.replaceState(serialized.slice(0, qIndex), serialized.slice(qIndex + 1));
    } else {
      this.location.replaceState(serialized, '');
    }
  }

  private updateJobInList(jobId: string, updates: Partial<EmailJobSummary>): void {
    const idx = this.jobs.findIndex(j => j.id === jobId);
    if (idx !== -1) {
      this.jobs[idx] = { ...this.jobs[idx], ...updates };
    }
  }
}
