/**
 * Captures the unsubscribe short-link credential out of the browser URL and rewrites the address bar
 * before Angular bootstraps.
 *
 * Why this runs before bootstrap rather than in a component:
 *
 * The credential rides in a path segment (`/u/{id}`), and `location.pathname` is read by observers we
 * do not control and cannot fully enumerate. The Application Insights SDK is the concrete one: it
 * seeds its operation name from `location.pathname` when it initialises and, with
 * `enableAutoRouteTracking` disabled, never updates it - so every dependency, event and exception
 * from that page load carries the raw credential, regardless of what the page-view URI says. A
 * component's `location.replaceState` runs far too late to prevent that, and redacting each
 * telemetry field after the fact means asserting the shape of SDK internals, which is how a previous
 * attempt at browser-side redaction produced seven distinct leaks over six review rounds (see
 * docs/email-suppression-and-unsubscribe.md).
 *
 * So this does not redact observers. It removes the thing being observed, using only `location` and
 * `history` - and it does so before any of them start. Whatever reads the URL afterwards sees
 * `/email-preferences`.
 */

/** Where the credential lands once the URL has been rewritten. Module-scoped, never persisted. */
let capturedLinkId: string | null = null;

/** The route the short link uses. Defined once, and asserted against the router table by a test. */
export const SHORT_LINK_PREFIX = '/u/';

/** Where the address bar is rewritten to, matching the legacy preferences route. */
const PREFERENCES_PATH = '/email-preferences';

/**
 * Reads and strips the credential. Safe to call when there is none, and idempotent - a second call
 * finds a rewritten URL and changes nothing.
 */
export function captureUnsubscribeCredentialFromUrl(pathname?: string): void {
  if (typeof window === 'undefined' || !window.location) {
    return;
  }

  // The path is a parameter with a default rather than read inline, because `location.pathname` is
  // not configurable and so cannot be stubbed - and a function whose only interesting input cannot
  // be varied is a function whose behaviour is not actually covered.
  const path = pathname ?? window.location.pathname;
  if (!path.toLowerCase().startsWith(SHORT_LINK_PREFIX)) {
    return;
  }

  // Everything after the prefix, up to the next slash. Decoded because the id is emitted
  // percent-encoded, and compared against nothing - any non-empty value is handed to the server,
  // which is the only thing that can say whether it resolves.
  const rest = path.substring(SHORT_LINK_PREFIX.length);
  const id = decodeURIComponent(rest.split('/')[0] ?? '');

  if (id) {
    capturedLinkId = id;
  }

  // Rewrite even when the id was empty: a stripped link is exactly the case this feature exists to
  // detect, and leaving `/u/` in the address bar serves nobody. The query and fragment go too - the
  // credential is never carried there, but neither is anything this page needs.
  if (window.history && typeof window.history.replaceState === 'function') {
    window.history.replaceState(null, '', PREFERENCES_PATH);
  }
}

/** The captured credential, or null if the page was not opened from a short link. */
export function takeCapturedUnsubscribeLinkId(): string | null {
  return capturedLinkId;
}

/** Test seam: clears the captured credential between specs. */
export function resetCapturedUnsubscribeLinkId(): void {
  capturedLinkId = null;
}
