import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { EmailPreferences } from '../models';

/**
 * Which shape of credential the page is holding. Discrimination is by parameter name and never by
 * inspecting the value, matching the server-side resolver - so the two ends cannot disagree about
 * what a given credential is, and a rejection names the shape that was actually presented.
 */
export type UnsubscribeCredential =
  | { kind: 'shortLink'; id: string }
  | { kind: 'legacyToken'; token: string };

@Injectable({
  providedIn: 'root',
})
export class EmailPreferencesService {
  constructor(private readonly httpClient: HttpClient) {}

  getPreferences(credential: UnsubscribeCredential): Observable<EmailPreferences> {
    return this.httpClient.get<EmailPreferences>(`api/email/preferences`, {
      params: toParams(credential),
    });
  }

  updatePreferences(credential: UnsubscribeCredential, prefs: EmailPreferences): Observable<unknown> {
    return this.httpClient.put(`api/email/preferences`, prefs, {
      params: toParams(credential),
    });
  }

  /**
   * Lifts the caller's own ResidentRequest suppression ("Resume receiving email"). Idempotent 200
   * when nothing is suppressed; 400 when the suppression is not resident-clearable (bounce or
   * complaint - admin only); 409 on write contention ("try again").
   */
  resume(credential: UnsubscribeCredential): Observable<unknown> {
    return this.httpClient.post(`api/email/preferences/resume`, null, {
      params: toParams(credential),
    });
  }
}

/**
 * NOTE - known, unclosed exposure. Whichever shape is used, the credential goes out as a query
 * value on this XHR, and the Application Insights JS SDK records the full request URL on dependency
 * telemetry. Moving the credential into the `/u/{id}` path fixed the *emailed link*, not this call.
 *
 * This is the pre-existing browser-side gap documented in docs/email-suppression-and-unsubscribe.md
 * and not a regression: the legacy scheme sent `?token=` from exactly here. Closing it means moving
 * the credential to a request header, which telemetry does not record - worth doing, but it is a
 * change to the API contract rather than to the link format, so it is tracked separately.
 */
function toParams(credential: UnsubscribeCredential): Record<string, string> {
  return credential.kind === 'shortLink' ? { u: credential.id } : { token: credential.token };
}
