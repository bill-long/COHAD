import { Component, OnDestroy, OnInit } from '@angular/core';
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
export class EmailJobListComponent implements OnInit, OnDestroy {
  jobs: EmailJobSummary[] = [];
  loading = true;
  errorText: string | null = null;

  private subscriptions: Subscription[] = [];

  constructor(
    private emailJobService: EmailJobService,
    private emailJobNotifications: EmailJobNotificationsService,
    private router: Router
  ) { }

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

  private updateJobInList(jobId: string, updates: Partial<EmailJobSummary>): void {
    const idx = this.jobs.findIndex(j => j.id === jobId);
    if (idx !== -1) {
      this.jobs[idx] = { ...this.jobs[idx], ...updates };
    }
  }
}
