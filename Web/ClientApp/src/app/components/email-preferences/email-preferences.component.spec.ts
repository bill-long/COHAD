import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { Location } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { of, throwError } from 'rxjs';
import { EmailPreferencesComponent } from './email-preferences.component';
import { EmailPreferencesService } from 'src/app/services/email-preferences.service';
import { EmailPreferences } from 'src/app/models';

const makePrefs = (overrides: Partial<EmailPreferences> = {}): EmailPreferences => ({
  email: 'jane@example.com',
  homeName: '123 Oak Avenue',
  boardEmailOptedIn: true,
  welcomeEmailOptedIn: true,
  gardenClubEmailOptedIn: true,
  socialCommitteeEmailOptedIn: true,
  sunshineCommitteeEmailOptedIn: true,
  suppressed: false,
  suppressedUtc: null,
  suppressionReason: null,
  canResume: false,
  ...overrides,
});

describe('EmailPreferencesComponent (suppression banner + resume)', () => {
  let component: EmailPreferencesComponent;
  let prefsServiceSpy: jasmine.SpyObj<EmailPreferencesService>;

  beforeEach(() => {
    prefsServiceSpy = jasmine.createSpyObj('EmailPreferencesService', ['getPreferences', 'updatePreferences', 'resume']);

    TestBed.configureTestingModule({
      providers: [
        EmailPreferencesComponent,
        { provide: EmailPreferencesService, useValue: prefsServiceSpy },
        { provide: Location, useValue: jasmine.createSpyObj('Location', ['replaceState']) },
        {
          provide: ActivatedRoute,
          // Arrives via the short link: /u/:id.
          useValue: { snapshot: { paramMap: convertToParamMap({ id: 'short1' }), queryParamMap: convertToParamMap({}) } },
        },
      ],
    });

    component = TestBed.inject(EmailPreferencesComponent);
  });

  it('exposes the suppression state returned by the GET', () => {
    prefsServiceSpy.getPreferences.and.returnValue(
      of(makePrefs({ suppressed: true, suppressedUtc: '2026-08-01T00:00:00Z', suppressionReason: 'ResidentRequest', canResume: true })),
    );

    component.ngOnInit();

    expect(component.prefs?.suppressed).toBeTrue();
    expect(component.prefs?.canResume).toBeTrue();
  });

  it('resume() posts with the same credential and reloads the preferences', () => {
    prefsServiceSpy.getPreferences.and.returnValues(
      of(makePrefs({ suppressed: true, suppressionReason: 'ResidentRequest', canResume: true })),
      of(makePrefs()),
    );
    prefsServiceSpy.resume.and.returnValue(of({}));
    component.ngOnInit();

    component.resume();

    expect(prefsServiceSpy.resume).toHaveBeenCalledWith({ kind: 'shortLink', id: 'short1' });
    // Reloaded from the server rather than locally guessing the new state.
    expect(prefsServiceSpy.getPreferences).toHaveBeenCalledTimes(2);
    expect(component.prefs?.suppressed).toBeFalse();
    expect(component.successMessage).toBeTruthy();
  });

  it('resume() with a failed reload shows a single refresh error, not a contradictory success+invalid-link pair', () => {
    prefsServiceSpy.getPreferences.and.returnValues(
      of(makePrefs({ suppressed: true, suppressionReason: 'ResidentRequest', canResume: true })),
      throwError(() => new HttpErrorResponse({ status: 500 })),
    );
    prefsServiceSpy.resume.and.returnValue(of({}));
    component.ngOnInit();

    component.resume();

    // The resume succeeded but the reload failed: exactly one banner, and it does not claim the
    // link is invalid (the credential just worked).
    expect(component.successMessage).toBe('');
    expect(component.errorMessage).toContain('could not be refreshed');
    expect(component.errorMessage).not.toContain('invalid');
  });

  it('resume() surfaces a 409 as try-again and does not reload', () => {
    prefsServiceSpy.getPreferences.and.returnValue(
      of(makePrefs({ suppressed: true, suppressionReason: 'ResidentRequest', canResume: true })),
    );
    prefsServiceSpy.resume.and.returnValue(throwError(() => new HttpErrorResponse({ status: 409 })));
    component.ngOnInit();

    component.resume();

    expect(component.errorMessage).toContain('try again');
    expect(prefsServiceSpy.getPreferences).toHaveBeenCalledTimes(1);
  });

  it('resume() reloads on a 400 so the page shows the server truth (state changed underneath)', () => {
    // The Resume button only renders for canResume, so a 400 means the suppression changed since
    // the page loaded (e.g. cleared, then re-suppressed by a bounce). The reload shows the
    // not-resumable banner instead of an error.
    prefsServiceSpy.getPreferences.and.returnValues(
      of(makePrefs({ suppressed: true, suppressionReason: 'ResidentRequest', canResume: true })),
      of(makePrefs({ suppressed: true, suppressionReason: 'HardBounce', canResume: false })),
    );
    prefsServiceSpy.resume.and.returnValue(throwError(() => new HttpErrorResponse({ status: 400 })));
    component.ngOnInit();

    component.resume();

    expect(prefsServiceSpy.getPreferences).toHaveBeenCalledTimes(2);
    expect(component.prefs?.canResume).toBeFalse();
    expect(component.errorMessage).toBe('');
  });

  it('resume() does nothing without a credential', () => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        EmailPreferencesComponent,
        { provide: EmailPreferencesService, useValue: prefsServiceSpy },
        { provide: Location, useValue: jasmine.createSpyObj('Location', ['replaceState']) },
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { paramMap: convertToParamMap({}), queryParamMap: convertToParamMap({}) } },
        },
      ],
    });
    const bare = TestBed.inject(EmailPreferencesComponent);
    bare.ngOnInit();

    bare.resume();

    expect(prefsServiceSpy.resume).not.toHaveBeenCalled();
  });
});
