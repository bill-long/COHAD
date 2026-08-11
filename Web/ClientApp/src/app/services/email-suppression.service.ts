import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { EmailSuppression } from '../models';

/**
 * Client for the Administrator-only suppression list (api/email-suppressions). Create and clear
 * return 409 on write contention (two admins racing); callers surface that as "try again", not as
 * an error state.
 */
@Injectable({ providedIn: 'root' })
export class EmailSuppressionService {
  constructor(private httpClient: HttpClient) {}

  getSuppressions(includeCleared = false): Observable<EmailSuppression[]> {
    const params = includeCleared ? new HttpParams().set('includeCleared', 'true') : undefined;
    return this.httpClient.get<EmailSuppression[]>('api/email-suppressions', { params });
  }

  createSuppression(email: string): Observable<EmailSuppression> {
    return this.httpClient.post<EmailSuppression>('api/email-suppressions', { email });
  }

  /**
   * suppressedUtc identifies the episode being cleared (the row's displayed value): the server
   * answers 409 if the record was re-suppressed since, so a stale page cannot lift a newer
   * suppression it never showed.
   */
  clearSuppression(id: string, suppressedUtc: string): Observable<EmailSuppression> {
    return this.httpClient.post<EmailSuppression>(`api/email-suppressions/${id}/clear`, { suppressedUtc });
  }
}
