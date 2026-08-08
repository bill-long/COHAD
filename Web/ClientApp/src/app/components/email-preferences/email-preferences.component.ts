import { Component, OnInit } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { Location } from '@angular/common';
import { EmailPreferencesService, UnsubscribeCredential } from '../../services/email-preferences.service';
import { EmailPreferences } from '../../models';

@Component({
  selector: 'app-email-preferences',
  templateUrl: './email-preferences.component.html',
  styleUrls: ['./email-preferences.component.scss'],
  standalone: false,
})
export class EmailPreferencesComponent implements OnInit {
  credential: UnsubscribeCredential | null = null;
  prefs: EmailPreferences | null = null;
  loading = true;
  saving = false;
  errorMessage = '';
  successMessage = '';

  categories = [
    { key: 'boardEmailOptedIn' as const, label: 'Board', tooltip: 'Annual Meeting announcements and other neighborhood business' },
    { key: 'welcomeEmailOptedIn' as const, label: 'Welcome Committee', tooltip: 'New arrivals in the neighborhood' },
    { key: 'gardenClubEmailOptedIn' as const, label: 'Garden Club', tooltip: 'Garden Club meetings and events' },
    { key: 'socialCommitteeEmailOptedIn' as const, label: 'Social Committee', tooltip: 'Social Committee events' },
    { key: 'sunshineCommitteeEmailOptedIn' as const, label: 'Sunshine Committee', tooltip: 'Sunshine Committee events' },
  ];

  constructor(
    private route: ActivatedRoute,
    private location: Location,
    private prefsService: EmailPreferencesService,
  ) {}

  ngOnInit(): void {
    this.credential = this.readCredential();
    if (!this.credential) {
      this.loading = false;
      this.errorMessage = 'No credential provided. Please use the link from your email.';
      return;
    }

    // Strip the credential from the address bar to reduce leakage risk (browser history, referrer
    // headers, copy/paste). Keep it in memory for the API calls.
    //
    // This does NOT keep the credential out of telemetry, and it is worth being exact about why,
    // because an earlier version of this comment claimed it did. The page-view URI is captured from
    // the router before this runs, so redaction has to happen there - see
    // ApplicationInsightsService.redactShortLinkId. And the credential still goes out as a query
    // value on this page's own API calls, which is the pre-existing browser-side gap documented in
    // docs/email-suppression-and-unsubscribe.md.
    this.location.replaceState('/email-preferences');

    this.prefsService.getPreferences(this.credential).subscribe({
      next: data => {
        this.prefs = data;
        this.loading = false;
      },
      error: () => {
        this.loading = false;
        this.errorMessage = 'Unable to load your email preferences. The link may be invalid or expired.';
      },
    });
  }

  /**
   * Reads whichever credential shape the URL carries. The short link arrives as the `:id` path
   * segment of `/u/:id`; the legacy link as `?token=` on `/email-preferences`. Discrimination is by
   * where the value came from, never by inspecting it, matching the server-side resolver - and the
   * short link wins for the same reason it does there, so one URL can only ever mean one thing.
   */
  private readCredential(): UnsubscribeCredential | null {
    const id = this.route.snapshot.paramMap.get('id');
    if (id) return { kind: 'shortLink', id };

    const token = this.route.snapshot.queryParamMap.get('token');
    if (token) return { kind: 'legacyToken', token };

    return null;
  }

  save(): void {
    if (!this.prefs || !this.credential || this.saving) return;
    this.saving = true;
    this.successMessage = '';
    this.errorMessage = '';

    this.prefsService.updatePreferences(this.credential, this.prefs).subscribe({
      next: () => {
        this.saving = false;
        this.successMessage = 'Your preferences have been saved.';
      },
      error: () => {
        this.saving = false;
        this.errorMessage = 'Unable to save your preferences. Please try again.';
      },
    });
  }
}
