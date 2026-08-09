import { TestBed } from '@angular/core/testing';
import { HttpClientTestingModule, HttpTestingController } from '@angular/common/http/testing';
import { EmailPreferencesService } from './email-preferences.service';
import { EmailPreferences } from '../models';

describe('EmailPreferencesService', () => {
  let service: EmailPreferencesService;
  let httpMock: HttpTestingController;

  const prefs: EmailPreferences = {
    email: 'jane@example.com',
    homeName: '123 Oak Avenue',
    boardEmailOptedIn: true,
    welcomeEmailOptedIn: true,
    gardenClubEmailOptedIn: true,
    socialCommitteeEmailOptedIn: true,
    sunshineCommitteeEmailOptedIn: true,
  };

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [HttpClientTestingModule],
    });
    service = TestBed.inject(EmailPreferencesService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  // The parameter name is the discriminator on both ends - the server resolves by which parameter
  // carried a value and never by inspecting it. Sending a short link as ?token= would be resolved as
  // a legacy token, fail, and be logged against the wrong credential type in the counter that
  // decides when legacy support can be retired.

  it('sends a short link as the id parameter', () => {
    service.getPreferences({ kind: 'shortLink', id: 'abc123' }).subscribe();

    const req = httpMock.expectOne('api/email/preferences?u=abc123');
    expect(req.request.method).toBe('GET');
    expect(req.request.params.get('token')).toBeNull();
    req.flush(prefs);
  });

  it('sends a legacy credential as the token parameter', () => {
    service.getPreferences({ kind: 'legacyToken', token: 'long-legacy-token' }).subscribe();

    const req = httpMock.expectOne('api/email/preferences?token=long-legacy-token');
    expect(req.request.method).toBe('GET');
    expect(req.request.params.get('u')).toBeNull();
    req.flush(prefs);
  });

  it('saves with the same credential shape it read with', () => {
    service.updatePreferences({ kind: 'shortLink', id: 'abc123' }, prefs).subscribe();

    const req = httpMock.expectOne('api/email/preferences?u=abc123');
    expect(req.request.method).toBe('PUT');
    expect(req.request.params.get('token')).toBeNull();
    req.flush({});
  });
});
