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
 * So this does not redact observers. It removes the thing being observed, using only `location`,
 * `history` and `sessionStorage` - and it does so before any of them start. Whatever reads the URL
 * afterwards sees `/email-preferences`.
 *
 * The captured id lives primarily in sessionStorage, for two reasons found in review. A module
 * variable dies with the document, so a bootstrap or chunk-load failure after the URL rewrite left
 * the user with no way to recover by refreshing - the credential was gone from the URL and from
 * memory both. And it had no owner to clear it, so it stayed readable for the whole tab session on
 * a shared machine. sessionStorage survives a refresh (tab-scoped, gone when the tab closes), and
 * the preferences page deletes it the moment a load succeeds, which is the point at which
 * refresh-recovery stops being needed. A module-level copy remains only as the fallback for
 * storage-blocked browsers, carrying the current load; both copies answer to the same clear call.
 *
 * This module MUST NOT throw: it runs above `bootstrapModule`, so an escaped exception is a blank
 * page on every route - and for exactly the mangled-link inputs this feature exists to survive.
 */

/** The route the short link uses. Defined once, and asserted against the router table by a test. */
export const SHORT_LINK_PREFIX = '/u/';

/** Where the address bar is rewritten to, matching the legacy preferences route. */
const PREFERENCES_PATH = '/email-preferences';

/** sessionStorage key for the captured id. */
const STORAGE_KEY = 'cohad.unsubscribe-link-id';

/**
 * Reads and strips the credential. Safe to call when there is none, idempotent, and guaranteed not
 * to throw - see the module comment for why that guarantee is load-bearing.
 *
 * The path is a parameter with a default rather than read inline, because `location.pathname` is
 * not configurable and so cannot be stubbed - and a function whose only interesting input cannot be
 * varied is a function whose behaviour is not actually covered.
 */
export function captureUnsubscribeCredentialFromUrl(pathname?: string): void {
  try {
    if (typeof window === 'undefined' || !window.location) {
      return;
    }

    const path = pathname ?? window.location.pathname;
    if (!path.toLowerCase().startsWith(SHORT_LINK_PREFIX)) {
      return;
    }

    // Everything after the prefix, up to the next slash. The id is emitted in the base64url
    // alphabet, which percent-encoding leaves untouched, so decoding is purely defensive - and a
    // mangled link can carry a bare '%' that makes decodeURIComponent throw URIError. That input is
    // the exact one this feature exists to survive, so on a failed decode the raw segment is kept:
    // the server rejects it with a named reason, which beats a blank page every time.
    const rawSegment = path.substring(SHORT_LINK_PREFIX.length).split('/')[0] ?? '';
    let id = rawSegment;
    try {
      id = decodeURIComponent(rawSegment);
    } catch {
      // Keep the raw segment.
    }

    // Storage first, an in-memory fallback when storage is blocked (disabled by policy, some
    // privacy modes - setItem can throw or silently no-op, hence the verified write). The fallback
    // serves the current page load only; it dies with the document, so in a storage-blocked
    // browser a refresh after a failed load is a dead end whose recovery is clicking the link in
    // the email again - which still works, and which is exactly what the page's error message says
    // to do.
    //
    // The URL rewrite is deliberately UNCONDITIONAL. An earlier revision kept the URL when the
    // storage write failed, to preserve the `/u/:id` route param as a fallback - and review found
    // it traded away the one invariant this module exists for (nothing may observe the credential
    // in location.pathname; the telemetry SDK seeds its operation name from it) and did not even
    // buy the availability it aimed for, because a case-mangled `/U/{id}` URL survives the capture
    // but matches no case-sensitive Angular route, rendering a blank page. Stripping always, with
    // the memory fallback carrying the current load, keeps both properties at once.
    if (id) {
      writeStoredId(id);
    }
    if (window.history && typeof window.history.replaceState === 'function') {
      window.history.replaceState(null, '', PREFERENCES_PATH);
    }
  } catch {
    // Never let a capture failure stop the app from booting. The worst outcome of swallowing is
    // that the credential stays in the URL for this load, which is the pre-capture status quo.
  }
}

/**
 * In-memory fallback for storage-blocked browsers, carrying the credential for the current page
 * load only. Never the primary store: it dies with the document, so it cannot serve
 * refresh-recovery - but it is also unobservable from the URL, which is the invariant that matters.
 */
let inMemoryLinkId: string | null = null;

/**
 * The captured credential, or null if the page was not opened from a short link. Does not clear -
 * the id must survive a refresh until a preferences load succeeds, so clearing belongs to the page
 * that knows when that happened. Call {@link clearCapturedUnsubscribeLinkId} there.
 */
export function readCapturedUnsubscribeLinkId(): string | null {
  try {
    return window.sessionStorage.getItem(STORAGE_KEY) ?? inMemoryLinkId;
  } catch {
    return inMemoryLinkId;
  }
}

/**
 * Deletes the captured credential. The preferences page calls this on a successful load: refresh
 * recovery is no longer needed past that point, and on a shared machine the credential must not
 * outlive the visit that used it.
 */
export function clearCapturedUnsubscribeLinkId(): void {
  inMemoryLinkId = null;
  try {
    window.sessionStorage.removeItem(STORAGE_KEY);
  } catch {
    // Nothing stored beyond the in-memory copy, which is already cleared.
  }
}

/** Writes to sessionStorage, falling back to the in-memory copy when the write does not land. */
function writeStoredId(id: string): void {
  try {
    window.sessionStorage.setItem(STORAGE_KEY, id);
    // Read-back rather than trusting a silent setItem: some browsers no-op instead of throwing
    // when storage is disabled, and an unnoticed dropped write here costs the user their only
    // remaining copy of the credential - the URL one is about to be stripped unconditionally.
    if (window.sessionStorage.getItem(STORAGE_KEY) === id) {
      return;
    }
  } catch {
    // Fall through to the in-memory copy.
  }

  inMemoryLinkId = id;
}
