import { Component, Inject, OnDestroy, OnInit } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { Observable, Subscription } from 'rxjs';
import { DeliveryStatus, EmailDeliveryEventDetail, EmailJobDetail, EmailJobStatus, EmailJobRecipientStatus } from 'src/app/models';
import { EmailJobService } from 'src/app/services/email-job.service';
import { EmailJobNotificationsService } from 'src/app/services/email-job-notifications.service';
import { httpErrorMessage } from 'src/app/utils/http-error-message';
import { EMAIL_JOBS_FOCUS_JOB_QUERY_PARAM, EMAIL_JOBS_SECTION_ANCHOR } from 'src/app/constants/email-jobs-send-page.constants';
import { ApplicationState, applicationState } from 'src/app/state';

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

  // Delivery events (admin-only, lazy-loaded per-recipient expand)
  isAdmin = false;
  expandedRecipients = new Set<string>();
  deliveryEvents: EmailDeliveryEventDetail[] | null = null;
  deliveryEventsLoading = false;
  deliveryEventsError: string | null = null;
  payloadsLoaded = false;
  payloadsLoading = false;
  payloadsError: string | null = null;
  private deliveryEventsByEmail = new Map<string, EmailDeliveryEventDetail[]>();
  // Monotonically increasing request id for delivery-event fetches. Concurrent
  // calls (e.g., user-initiated payload load racing with a SignalR completion
  // refresh) can complete out of order; we only apply the response from the
  // most recently issued request so an older fetch can't overwrite newer state.
  private deliveryEventsRequestSeq = 0;

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
    @Inject(applicationState) private readonly appState$: Observable<ApplicationState>,
  ) {}

  ngOnInit(): void {
    this.jobId = this.route.snapshot.paramMap.get('id')!;
    this.loadJob();
    this.emailJobNotifications.connect();

    this.subscriptions.push(
      this.appState$.subscribe(s => {
        this.isAdmin = s.apiUser?.roles?.includes('Administrator') ?? false;
      }),
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
              // Refresh delivery events if any recipients are expanded.
              // Preserve the user's payload-loaded preference across the refresh,
              // and treat an in-flight payload load as "preserve payloads" too —
              // otherwise this refresh could finish after the payload load and
              // overwrite payload data with payload-less data.
              if (this.expandedRecipients.size > 0) {
                this.loadDeliveryEvents(this.payloadsLoaded || this.payloadsLoading);
              }
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

  deliveryStatusClass(status: DeliveryStatus): string {
    switch (status) {
      case 'Delivered':
        return 'delivery-delivered';
      case 'Bounced':
        return 'delivery-bounced';
      case 'Deferred':
        return 'delivery-deferred';
      case 'SpamReport':
        return 'delivery-spam';
      case 'Rejected':
        return 'delivery-rejected';
      default:
        return '';
    }
  }

  deliveryStatusLabel(status: DeliveryStatus): string {
    switch (status) {
      case 'SpamReport':
        return 'Spam';
      default:
        return status;
    }
  }

  toggleRecipientEvents(email: string): void {
    // Defense-in-depth: button is only rendered for admins, but the method is
    // public and could be invoked programmatically. Avoid issuing fetches that
    // the backend would reject anyway.
    if (!this.isAdmin) return;
    const key = email.toLowerCase();
    if (this.expandedRecipients.has(key)) {
      this.expandedRecipients.delete(key);
    } else {
      this.expandedRecipients.add(key);
      if (this.deliveryEvents === null) {
        this.loadDeliveryEvents();
      }
    }
  }

  /**
   * Loads delivery events for the current job. Defaults to events-only (no raw
   * provider payloads) to keep the response small. Pass `includePayload=true`
   * to also fetch the full webhook JSON for each event (used by the explicit
   * "Load raw payloads" action and to preserve payload mode across refreshes).
   *
   * Concurrent calls are allowed (e.g., a user-initiated payload load can race
   * with a SignalR completion-event refresh). Each call captures a monotonic
   * request id and only the response from the most recently issued request is
   * applied to component state, so an older fetch can never overwrite the
   * data, error, or payload-mode flags from a newer one.
   */
  loadDeliveryEvents(includePayload = false): void {
    // When fetching payloads after events have already loaded, keep the events
    // table visible and use a separate spinner so the UI doesn't flash empty.
    if (includePayload && this.deliveryEvents !== null) {
      this.payloadsLoading = true;
      this.payloadsError = null;
    } else {
      this.deliveryEventsLoading = true;
      this.deliveryEventsError = null;
    }
    const requestId = ++this.deliveryEventsRequestSeq;
    this.emailJobService.getDeliveryEvents(this.jobId, includePayload).subscribe({
      next: events => {
        // Discard stale responses so an older request can't overwrite newer state.
        if (requestId !== this.deliveryEventsRequestSeq) {
          return;
        }
        this.deliveryEvents = events;
        this.buildEventsByEmailMap(events);
        this.deliveryEventsLoading = false;
        this.payloadsLoading = false;
        this.payloadsLoaded = includePayload;
        // Defensive: success unambiguously clears any prior error state.
        this.deliveryEventsError = null;
        this.payloadsError = null;
      },
      error: err => {
        if (requestId !== this.deliveryEventsRequestSeq) {
          return;
        }
        // Failed payload re-fetches must not blank the events table that's
        // already on screen. Surface the error in a payload-scoped slot
        // instead of the main events-load error slot.
        if (includePayload && this.deliveryEvents !== null) {
          this.payloadsError = httpErrorMessage(err, 'Failed to load delivery payloads.');
        } else {
          this.deliveryEventsError = httpErrorMessage(err, 'Failed to load delivery events.');
        }
        this.deliveryEventsLoading = false;
        this.payloadsLoading = false;
      },
    });
  }

  getEventsForRecipient(email: string): EmailDeliveryEventDetail[] {
    return this.deliveryEventsByEmail.get(email.toLowerCase()) ?? [];
  }

  private buildEventsByEmailMap(events: EmailDeliveryEventDetail[]): void {
    this.deliveryEventsByEmail.clear();
    for (const evt of events) {
      const key = evt.email.toLowerCase();
      if (!this.deliveryEventsByEmail.has(key)) {
        this.deliveryEventsByEmail.set(key, []);
      }
      this.deliveryEventsByEmail.get(key)!.push(evt);
    }
    // Sort each recipient's events chronologically
    for (const evts of this.deliveryEventsByEmail.values()) {
      evts.sort((a, b) => new Date(a.receivedUtc).getTime() - new Date(b.receivedUtc).getTime());
    }
  }
}
