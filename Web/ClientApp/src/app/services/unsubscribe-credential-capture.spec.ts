import { routes } from '../app-routing.module';
import {
  SHORT_LINK_PREFIX,
  captureUnsubscribeCredentialFromUrl,
  resetCapturedUnsubscribeLinkId,
  takeCapturedUnsubscribeLinkId,
} from './unsubscribe-credential-capture';

describe('unsubscribe credential capture', () => {
  let replaceState: jasmine.Spy;

  beforeEach(() => {
    resetCapturedUnsubscribeLinkId();
    replaceState = spyOn(window.history, 'replaceState');
  });

  it('captures the id and rewrites the address bar off the credential', () => {
    captureUnsubscribeCredentialFromUrl('/u/Ab3-x9_KqRs7TuVwXyZ01');

    expect(takeCapturedUnsubscribeLinkId()).toBe('Ab3-x9_KqRs7TuVwXyZ01');
    expect(replaceState).toHaveBeenCalledWith(null, '', '/email-preferences');
  });

  it('rewrites even when the id is missing, so a stripped link does not linger in the URL', () => {
    captureUnsubscribeCredentialFromUrl('/u/');

    expect(takeCapturedUnsubscribeLinkId()).toBeNull();
    expect(replaceState).toHaveBeenCalled();
  });

  it('decodes a percent-encoded id', () => {
    captureUnsubscribeCredentialFromUrl('/u/' + encodeURIComponent('a b+c'));

    expect(takeCapturedUnsubscribeLinkId()).toBe('a b+c');
  });

  it('ignores a trailing segment rather than folding it into the id', () => {
    captureUnsubscribeCredentialFromUrl('/u/abc123/extra');

    expect(takeCapturedUnsubscribeLinkId()).toBe('abc123');
  });

  it('leaves every other route alone', () => {
    captureUnsubscribeCredentialFromUrl('/residents/directory');

    expect(takeCapturedUnsubscribeLinkId()).toBeNull();
    expect(replaceState).not.toHaveBeenCalled();
  });

  it('is idempotent - a second call on the rewritten URL changes nothing', () => {
    captureUnsubscribeCredentialFromUrl('/email-preferences');

    expect(takeCapturedUnsubscribeLinkId()).toBeNull();
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
