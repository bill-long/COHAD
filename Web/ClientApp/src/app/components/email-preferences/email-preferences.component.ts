import { Component, OnInit } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { Location } from '@angular/common';
import { EmailPreferencesService, UnsubscribeCredential } from '../../services/email-preferences.service';
import {
  clearCapturedUnsubscribeLinkId,
  readCapturedUnsubscribeLinkId,
} from '../../services/unsubscribe-credential-capture';
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
    this.prefsService.getPreferences(this.credential).subscribe({
      next: data => {
        this.prefs = data;
        this.loading = false;
        // The load succeeded, so refresh-recovery is no longer needed: strip whatever credential
        // the URL still carries and drop the stored capture, in that order of importance. The
        // strip here covers the legacy ?token= case - the short link is removed before Angular
        // bootstraps (see unsubscribe-credential-capture), because this point is far too late for
        // anything that reads location.pathname at startup. It deliberately runs only on SUCCESS:
        // stripping before the load ran meant a transient failure left the resident with no
        // credential anywhere and nothing to refresh, so the URL keeps it until it has actually
        // been redeemed. The clear is the shared-machine half: the credential must not outlive
        // the visit that used it, and this component's own copy keeps serving the save calls.
        //
        // Neither keeps the credential out of this page's own API calls, where it goes out as a
        // query value - the pre-existing browser-side gap documented in
        // docs/email-suppression-and-unsubscribe.md.
        this.location.replaceState('/email-preferences');
        clearCapturedUnsubscribeLinkId();
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
    // URL-carried credentials first, the stored capture last. The order matters: the capture is
    // deliberately kept in sessionStorage after a FAILED load (so a refresh can retry), which means
    // it can be stale - and a stale stored id must not shadow a fresh credential the user just
    // arrived with. A `/u/:id` route param survives only when the pre-bootstrap capture could not
    // store it (storage-blocked browsers), and a `?token=` is a different link opened on purpose;
    // both are what the user is holding NOW. The stored id is only ever the memory of a previous
    // arrival, so it goes last.
    const id = this.route.snapshot.paramMap.get('id');
    if (id) return { kind: 'shortLink', id };

    const token = this.route.snapshot.queryParamMap.get('token');
    if (token) return { kind: 'legacyToken', token };

    const captured = readCapturedUnsubscribeLinkId();
    if (captured) return { kind: 'shortLink', id: captured };

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
