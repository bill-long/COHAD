import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { ClearEmailSuppressionResult, EmailSuppression } from '../models';

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

  clearSuppression(id: string): Observable<ClearEmailSuppressionResult> {
    return this.httpClient.post<ClearEmailSuppressionResult>(`api/email-suppressions/${id}/clear`, null);
  }
}
