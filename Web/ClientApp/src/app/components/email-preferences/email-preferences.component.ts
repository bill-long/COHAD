import { Component, OnInit } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { Location } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
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
  resuming = false;
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

    // Strip whatever credential the URL still carries, before the first request. This covers the
    // legacy ?token= case - the short link is removed before Angular even bootstraps (see
    // unsubscribe-credential-capture), because this point is far too late for anything that reads
    // location.pathname at startup. Stripping BEFORE the load, not on success, is deliberate and
    // was reverted back to after one round the other way: a token left in the URL on a failed load
    // sits in browser history and Referer headers for as long as the error page does, and the
    // refresh-retry that late stripping bought exists only for the failure case of a declining
    // legacy shape. Short links are unaffected either way - their refresh-recovery rides in
    // sessionStorage, which no URL rewrite touches.
    //
    // None of this keeps the credential out of this page's own API calls, where it goes out as a
    // query value - the pre-existing browser-side gap documented in
    // docs/email-suppression-and-unsubscribe.md.
    this.location.replaceState('/email-preferences');

    this.loadPreferences();
  }

  /**
   * Loads the preferences for the held credential. The banner is decided by THIS load's outcome,
   * keyed on why we are loading:
   * <ul>
   *   <li><c>'initial'</c> - the first load; a failure genuinely may be a bad link.</li>
   *   <li><c>'resumed'</c> - reload after a successful resume; success confirms it, a failure is a
   *   refresh problem (not an invalid link - the credential just worked).</li>
   *   <li><c>'refresh'</c> - reload after a rejected resume (400); the credential authenticated, so
   *   a failure is again a refresh problem, and success shows the (now not-resumable) state with no
   *   success banner.</li>
   * </ul>
   * Only <c>'initial'</c> attributes a failure to the link; the post-resume modes never do, so the
   * page cannot mislabel a demonstrably valid credential as invalid.
   */
  private loadPreferences(reason: 'initial' | 'resumed' | 'refresh' = 'initial'): void {
    if (!this.credential) return;
    this.loading = true;
    this.prefsService.getPreferences(this.credential).subscribe({
      next: data => {
        this.prefs = data;
        this.loading = false;
        // Only claim success when the reloaded state actually shows delivery restored: a
        // re-suppression (e.g. a hard bounce) landing between the resume POST and this GET would
        // otherwise show 'resumed' alongside the 'delivery is currently stopped' banner.
        if (reason === 'resumed' && !data.suppressed) this.successMessage = 'Email delivery has been resumed.';
        // The load succeeded, so refresh-recovery is no longer needed and the credential must not
        // outlive the visit that used it on a shared machine. This component's own copy keeps
        // serving the save calls. On a FAILED load the capture is deliberately kept, so a refresh
        // can retry.
        clearCapturedUnsubscribeLinkId();
      },
      error: () => {
        this.loading = false;
        // Only the initial load blames the link. 'resumed' follows a confirmed-successful resume,
        // so it may say so. 'refresh' follows a 400 that is ambiguous (not-resident-clearable OR an
        // invalidated credential), so it must not claim the request succeeded.
        this.errorMessage =
          reason === 'initial'
            ? 'Unable to load your email preferences. The link may be invalid or expired.'
            : reason === 'resumed'
              ? 'Email delivery was resumed, but this page could not be refreshed. Reload to see your current settings.'
              : 'This page could not be refreshed. Reload to see your current settings.';
      },
    });
  }

  /**
   * Lifts the caller's own ResidentRequest suppression, then reloads so the page reflects the
   * server's new state rather than a local guess. A 400 means the suppression is no longer
   * resident-clearable (the state changed since the page loaded - e.g. cleared and re-suppressed
   * by a bounce), so a reload shows the correct banner; a 409 is expected write contention.
   */
  resume(): void {
    if (!this.credential || this.resuming) return;
    this.resuming = true;
    this.errorMessage = '';
    this.successMessage = '';

    this.prefsService.resume(this.credential).subscribe({
      next: () => {
        this.resuming = false;
        // The success banner is set by the reload's own outcome, so a failed reload never leaves a
        // success and an error banner showing together.
        this.loadPreferences('resumed');
      },
      error: err => {
        this.resuming = false;
        if (err instanceof HttpErrorResponse && err.status === 409) {
          this.errorMessage = 'Your request could not be applied because of a concurrent update. Please try again.';
          return;
        }
        if (err instanceof HttpErrorResponse && err.status === 400) {
          // The credential authenticated (400, not 401/404) but the suppression is no longer
          // resident-clearable - reload to show the current state; a failed reload here is a refresh
          // problem, never an invalid-link message.
          this.loadPreferences('refresh');
          return;
        }
        this.errorMessage = 'Unable to resume email delivery. Please try again.';
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
    // deliberately kept in sessionStorage after a FAILED load (so a refresh can retry), which
    // means it can be stale - and a stale stored id must not shadow a fresh credential the user
    // just arrived with. A `?token=` is a different link opened on purpose, and the `/u/:id` route
    // param is defence in depth for a navigation that reached this route without the
    // pre-bootstrap capture running; both are what the user is holding NOW. The stored id is only
    // ever the memory of an arrival, so it goes last - and the capture module orders its own two
    // stores the same way, current load before stored past.
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
