import { Component, OnInit } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { EmailPreferencesService } from '../../services/email-preferences.service';
import { EmailPreferences } from '../../models';

@Component({
  selector: 'app-email-preferences',
  templateUrl: './email-preferences.component.html',
  styleUrls: ['./email-preferences.component.scss'],
  standalone: false
})
export class EmailPreferencesComponent implements OnInit {
  token = '';
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
    { key: 'sunshineCommitteeEmailOptedIn' as const, label: 'Sunshine Committee', tooltip: 'Sunshine Committee events' }
  ];

  constructor(
    private route: ActivatedRoute,
    private prefsService: EmailPreferencesService
  ) { }

  ngOnInit(): void {
    this.token = this.route.snapshot.queryParamMap.get('token') ?? '';
    if (!this.token) {
      this.loading = false;
      this.errorMessage = 'No token provided. Please use the link from your email.';
      return;
    }

    this.prefsService.getPreferences(this.token).subscribe({
      next: (data) => {
        this.prefs = data;
        this.loading = false;
      },
      error: () => {
        this.loading = false;
        this.errorMessage = 'Unable to load your email preferences. The link may be invalid or expired.';
      }
    });
  }

  save(): void {
    if (!this.prefs || this.saving) return;
    this.saving = true;
    this.successMessage = '';
    this.errorMessage = '';

    this.prefsService.updatePreferences(this.token, this.prefs).subscribe({
      next: () => {
        this.saving = false;
        this.successMessage = 'Your preferences have been saved.';
      },
      error: () => {
        this.saving = false;
        this.errorMessage = 'Unable to save your preferences. Please try again.';
      }
    });
  }
}
