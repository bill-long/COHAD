import { Component, OnDestroy, OnInit } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { Subscription } from 'rxjs';
import { EmailJobDetail, EmailJobStatus, EmailJobRecipientStatus } from 'src/app/models';
import { EmailJobService } from 'src/app/services/email-job.service';
import { EmailJobNotificationsService } from 'src/app/services/email-job-notifications.service';
import { httpErrorMessage } from 'src/app/utils/http-error-message';
import { EMAIL_JOBS_FOCUS_JOB_QUERY_PARAM, EMAIL_JOBS_SECTION_ANCHOR } from 'src/app/constants/email-jobs-send-page.constants';

@Component({
  selector: 'app-email-job-detail',
  templateUrl: './email-job-detail.component.html',
  styleUrls: ['./email-job-detail.component.css'],
  standalone: false,
})
export class EmailJobDetailComponent implements OnInit, OnDestroy {
  job: EmailJobDetail | null = null;
  loading = true;
  errorText: string | null = null;
  actionInProgress = false;

  /** Queued for this long without starting counts as stale (client-side nudge only). */
  private readonly staleQueuedThresholdMs = 12 * 60 * 1000;

  readonly cancelJobTooltip = 'Stops processing and marks the job cancelled. You can run it again afterward.';

  private jobId!: string;
  private subscriptions: Subscription[] = [];

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private emailJobService: EmailJobService,
    private emailJobNotifications: EmailJobNotificationsService,
  ) {}

  ngOnInit(): void {
    this.jobId = this.route.snapshot.paramMap.get('id')!;
    this.loadJob();
    this.emailJobNotifications.connect();

    this.subscriptions.push(
      this.emailJobNotifications.progress$.subscribe(event => {
        if (this.job && event.jobId === this.job.id) {
          this.job = {
            ...this.job,
            status: event.status,
            sentCount: event.sentCount,
            failedCount: event.failedCount,
            totalRecipients: event.totalRecipients,
          };
        }
      }),
      this.emailJobNotifications.completed$.subscribe(event => {
        if (this.job && event.jobId === this.job.id) {
          this.job = {
            ...this.job,
            status: event.status,
            sentCount: event.sentCount,
            failedCount: event.failedCount,
            totalRecipients: event.totalRecipients,
            lastError: event.lastError,
          };
          // Reload full detail to get updated recipient statuses
          this.emailJobService.getJob(this.jobId).subscribe({
            next: detail => {
              this.job = detail;
            },
            error: err => {
              this.errorText = httpErrorMessage(err, 'Failed to refresh email job details.');
            },
          });
        }
      }),
    );
  }

  ngOnDestroy(): void {
    this.subscriptions.forEach(s => s.unsubscribe());
    this.emailJobNotifications.disconnect();
  }

  loadJob(): void {
    this.loading = true;
    this.errorText = null;
    this.emailJobService.getJob(this.jobId).subscribe({
      next: job => {
        this.job = job;
        this.loading = false;
      },
      error: err => {
        this.errorText = httpErrorMessage(err, 'Failed to load email job.');
        this.loading = false;
      },
    });
  }

  get progressPercent(): number {
    if (!this.job || this.job.totalRecipients === 0) return 0;
    return Math.round(((this.job.sentCount + this.job.failedCount) / this.job.totalRecipients) * 100);
  }

  get pendingCount(): number {
    if (!this.job) return 0;
    return this.job.totalRecipients - this.job.sentCount - this.job.failedCount;
  }

  get canRetry(): boolean {
    if (!this.job) return false;
    return ['Failed', 'PartiallyCompleted', 'Cancelled'].includes(this.job.status);
  }

  get canCancel(): boolean {
    if (!this.job) return false;
    return ['Queued', 'InProgress'].includes(this.job.status);
  }

  get retryButtonLabel(): string {
    if (!this.job) return 'Retry';
    if (this.job.status === 'Cancelled') return 'Run again';
    return 'Retry failed recipients';
  }

  /** Shown above actions while the job may still be running or waiting to start. */
  get showStuckRecoveryHint(): boolean {
    if (!this.job) return false;
    return this.job.status === 'Queued' || this.job.status === 'InProgress';
  }

  /** Queued a long time with no start time — stronger copy in the hint. */
  get isStaleQueuedJob(): boolean {
    if (!this.job || this.job.status !== 'Queued') return false;
    if (this.job.startedUtc) return false;
    const created = new Date(this.job.createdUtc).getTime();
    return Date.now() - created > this.staleQueuedThresholdMs;
  }

  retryJob(): void {
    if (!this.job || this.actionInProgress) return;
    this.actionInProgress = true;
    this.emailJobService.retryJob(this.job.id).subscribe({
      next: updated => {
        if (this.job) {
          this.job = { ...this.job, ...updated };
        }
        this.actionInProgress = false;
      },
      error: err => {
        this.errorText = httpErrorMessage(err, 'Failed to retry job.');
        this.actionInProgress = false;
      },
    });
  }

  cancelJob(): void {
    if (!this.job || this.actionInProgress) return;
    this.actionInProgress = true;
    this.emailJobService.cancelJob(this.job.id).subscribe({
      next: updated => {
        if (this.job) {
          this.job = { ...this.job, ...updated };
        }
        this.actionInProgress = false;
      },
      error: err => {
        this.errorText = httpErrorMessage(err, 'Failed to cancel job.');
        this.actionInProgress = false;
      },
    });
  }

  goBack(): void {
    this.router.navigate(['/manage/send-email'], {
      queryParams: { [EMAIL_JOBS_FOCUS_JOB_QUERY_PARAM]: this.jobId.toLowerCase() },
      fragment: EMAIL_JOBS_SECTION_ANCHOR,
    });
  }

  statusLabel(status: EmailJobStatus): string {
    switch (status) {
      case 'InProgress':
        return 'In Progress';
      case 'PartiallyCompleted':
        return 'Partial';
      default:
        return status;
    }
  }

  statusClass(status: EmailJobStatus): string {
    switch (status) {
      case 'Queued':
        return 'status-queued';
      case 'InProgress':
        return 'status-in-progress';
      case 'Completed':
        return 'status-completed';
      case 'PartiallyCompleted':
        return 'status-partial';
      case 'Failed':
        return 'status-failed';
      case 'Cancelled':
        return 'status-cancelled';
      default:
        return '';
    }
  }

  recipientStatusClass(status: EmailJobRecipientStatus): string {
    switch (status) {
      case 'Sent':
        return 'status-completed';
      case 'Failed':
        return 'status-failed';
      case 'Pending':
        return 'status-queued';
      default:
        return '';
    }
  }
}
