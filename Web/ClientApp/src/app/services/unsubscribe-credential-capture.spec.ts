import { routes } from '../app-routing.module';
import {
  SHORT_LINK_PREFIX,
  captureUnsubscribeCredentialFromUrl,
  clearCapturedUnsubscribeLinkId,
  readCapturedUnsubscribeLinkId,
} from './unsubscribe-credential-capture';

describe('unsubscribe credential capture', () => {
  let replaceState: jasmine.Spy;

  beforeEach(() => {
    clearCapturedUnsubscribeLinkId();
    replaceState = spyOn(window.history, 'replaceState');
  });

  afterEach(() => {
    clearCapturedUnsubscribeLinkId();
  });

  it('captures the id and rewrites the address bar off the credential', () => {
    captureUnsubscribeCredentialFromUrl('/u/Ab3-x9_KqRs7TuVwXyZ01');

    expect(readCapturedUnsubscribeLinkId()).toBe('Ab3-x9_KqRs7TuVwXyZ01');
    expect(replaceState).toHaveBeenCalledWith(null, '', '/email-preferences');
  });

  it('survives a refresh: the id is still readable on a second read', () => {
    // A bootstrap or chunk-load failure after the URL rewrite leaves refresh as the user's only
    // recovery, so the captured id must not die with the document.
    captureUnsubscribeCredentialFromUrl('/u/abc123');

    expect(readCapturedUnsubscribeLinkId()).toBe('abc123');
    expect(readCapturedUnsubscribeLinkId()).toBe('abc123');
  });

  it('is gone after an explicit clear, so it cannot serve a later visitor', () => {
    captureUnsubscribeCredentialFromUrl('/u/abc123');
    clearCapturedUnsubscribeLinkId();

    expect(readCapturedUnsubscribeLinkId()).toBeNull();
  });

  it('rewrites even when the id is missing, so a stripped link does not linger in the URL', () => {
    captureUnsubscribeCredentialFromUrl('/u/');

    expect(readCapturedUnsubscribeLinkId()).toBeNull();
    expect(replaceState).toHaveBeenCalled();
  });

  it('decodes a percent-encoded id', () => {
    captureUnsubscribeCredentialFromUrl('/u/' + encodeURIComponent('a b+c'));

    expect(readCapturedUnsubscribeLinkId()).toBe('a b+c');
  });

  it('keeps the raw segment when decoding throws, and still rewrites the URL', () => {
    // A truncated link can end in a bare '%', which makes decodeURIComponent throw URIError. That
    // input is exactly the mangled-link case this feature exists to survive: it must neither kill
    // the bootstrap (this runs above bootstrapModule) nor leave the credential in the address bar.
    expect(() => captureUnsubscribeCredentialFromUrl('/u/Ab3-x9_KqRs7Tu%')).not.toThrow();

    expect(readCapturedUnsubscribeLinkId()).toBe('Ab3-x9_KqRs7Tu%');
    expect(replaceState).toHaveBeenCalledWith(null, '', '/email-preferences');
  });

  it('still strips the URL and serves the id from memory when storage rejects the write', () => {
    // Storage disabled by policy or privacy mode. The strip is unconditional - the URL is what
    // telemetry observes, and an earlier revision that kept it traded the module's one invariant
    // for a fallback that did not even work (a case-mangled /U/ URL matches no Angular route).
    // The in-memory copy carries the current load instead.
    spyOn(Storage.prototype, 'setItem').and.throwError('storage disabled');
    spyOn(Storage.prototype, 'getItem').and.returnValue(null);

    captureUnsubscribeCredentialFromUrl('/u/abc123');

    expect(replaceState).toHaveBeenCalledWith(null, '', '/email-preferences');
    expect(readCapturedUnsubscribeLinkId()).toBe('abc123');
  });

  it('detects a silently dropped write and serves the id from memory', () => {
    // Some browsers no-op instead of throwing when storage is disabled - the write "succeeds" and
    // the value is simply not there. Trusting setItem would lose the only remaining copy.
    spyOn(Storage.prototype, 'setItem').and.stub();
    spyOn(Storage.prototype, 'getItem').and.returnValue(null);

    captureUnsubscribeCredentialFromUrl('/u/abc123');

    expect(replaceState).toHaveBeenCalledWith(null, '', '/email-preferences');
    expect(readCapturedUnsubscribeLinkId()).toBe('abc123');
  });

  it('ignores a trailing segment rather than folding it into the id', () => {
    captureUnsubscribeCredentialFromUrl('/u/abc123/extra');

    expect(readCapturedUnsubscribeLinkId()).toBe('abc123');
  });

  it('leaves every other route alone', () => {
    captureUnsubscribeCredentialFromUrl('/residents/directory');

    expect(readCapturedUnsubscribeLinkId()).toBeNull();
    expect(replaceState).not.toHaveBeenCalled();
  });

  it('is idempotent - a second call on the rewritten URL changes nothing', () => {
    captureUnsubscribeCredentialFromUrl('/email-preferences');

    expect(readCapturedUnsubscribeLinkId()).toBeNull();
    expect(replaceState).not.toHaveBeenCalled();
  });

  // The prefix is used by the capture above and by the telemetry redaction, while the route itself
  // is declared in the routing module. Nothing else would fail if they drifted, and the failure
  // mode is a published credential rather than a broken page.
  it('matches the route declared in the router table', () => {
    const declared = routes.map(r => r.path).filter((p): p is string => typeof p === 'string');

    expect(declared).toContain(`${SHORT_LINK_PREFIX.replace(/^\//, '')}:id`);
  });
});
