import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { EmailDeliveryEventDetail, EmailJobDetail, EmailJobSummary, TestRecipientOption } from '../models';

@Injectable({ providedIn: 'root' })
export class EmailJobService {
  constructor(private httpClient: HttpClient) {}

  getTestRecipients(): Observable<TestRecipientOption[]> {
    return this.httpClient.get<TestRecipientOption[]>('api/email/test-recipients');
  }

  getRecentJobs(limit = 50): Observable<EmailJobSummary[]> {
    return this.httpClient.get<EmailJobSummary[]>(`api/email/jobs?limit=${limit}`);
  }

  getJob(id: string): Observable<EmailJobDetail> {
    return this.httpClient.get<EmailJobDetail>(`api/email/jobs/${id}`);
  }

  retryJob(id: string): Observable<EmailJobSummary> {
    return this.httpClient.post<EmailJobSummary>(`api/email/jobs/${id}/retry`, null);
  }

  cancelJob(id: string): Observable<EmailJobSummary> {
    return this.httpClient.post<EmailJobSummary>(`api/email/jobs/${id}/cancel`, null);
  }

  getDeliveryEvents(jobId: string, includePayload = false): Observable<EmailDeliveryEventDetail[]> {
    const params = includePayload ? '?includePayload=true' : '';
    return this.httpClient.get<EmailDeliveryEventDetail[]>(`api/email/jobs/${jobId}/delivery-events${params}`);
  }
}
