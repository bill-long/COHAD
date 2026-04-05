import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { EmailPreferences } from '../models';

@Injectable({
  providedIn: 'root',
})
export class EmailPreferencesService {
  constructor(private readonly httpClient: HttpClient) {}

  getPreferences(token: string): Observable<EmailPreferences> {
    return this.httpClient.get<EmailPreferences>(`api/email/preferences`, {
      params: { token },
    });
  }

  updatePreferences(token: string, prefs: EmailPreferences): Observable<unknown> {
    return this.httpClient.put(`api/email/preferences`, prefs, {
      params: { token },
    });
  }
}
