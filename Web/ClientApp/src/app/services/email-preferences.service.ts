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
}

function toParams(credential: UnsubscribeCredential): Record<string, string> {
  return credential.kind === 'shortLink' ? { id: credential.id } : { token: credential.token };
}
