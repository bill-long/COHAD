import { DOCUMENT } from '@angular/common';
import { Component, Inject, Input, OnChanges, OnDestroy, OnInit, SimpleChanges } from '@angular/core';
import { Router } from '@angular/router';
import { Subscription } from 'rxjs';
import { EmailJobSummary, EmailJobStatus } from 'src/app/models';
import { EmailJobService } from 'src/app/services/email-job.service';
import { EmailJobNotificationsService } from 'src/app/services/email-job-notifications.service';
import { httpErrorMessage } from 'src/app/utils/http-error-message';

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

  private subscriptions: Subscription[] = [];

  constructor(
    private emailJobService: EmailJobService,
    private emailJobNotifications: EmailJobNotificationsService,
    private router: Router,
    @Inject(DOCUMENT) private document: Document
  ) { }

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['scrollTargetJobId']) {
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

  private scheduleScrollToTargetJob(): void {
    const id = this.scrollTargetJobId?.toLowerCase();
    if (!id || this.loading) {
      return;
    }
    const rowId = `email-job-row-${id}`;
    let scrolled = false;
    const tryOnce = (): void => {
      if (scrolled) {
        return;
      }
      const el = this.document.getElementById(rowId);
      if (el) {
        scrolled = true;
        el.scrollIntoView({ behavior: 'smooth', block: 'start' });
      }
    };
    requestAnimationFrame(() => requestAnimationFrame(tryOnce));
    const delaysMs = [16, 32, 64, 100, 200, 400, 700];
    for (const ms of delaysMs) {
      setTimeout(tryOnce, ms);
    }
  }

  jobRowDomId(job: EmailJobSummary): string {
    return `email-job-row-${job.id.toLowerCase()}`;
  }

  private updateJobInList(jobId: string, updates: Partial<EmailJobSummary>): void {
    const idx = this.jobs.findIndex(j => j.id === jobId);
    if (idx !== -1) {
      this.jobs[idx] = { ...this.jobs[idx], ...updates };
    }
  }
}
