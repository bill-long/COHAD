# Email suppression, unsubscribe recovery, and short links

Design doc. Status: **Part 1 implemented**; **Part 2a implemented** (short links, live in
production 2026-08-09); **Part 2b skipped** by decision (see Part 2); **Part 3 implemented**
(PR-3a - record, enforcement, both writers, resident resume, admin API; PR-3b - forwarding
deliverable-address preference and all frontend surfacing; the moderator in-app notification is
deferred by decision - see PR-3b notes); Parts 4-5 proposed.

## Problem

Two gaps, one of which was the trigger and one of which was found while investigating it.

**1. Failing addresses are never stopped for committee forwards.** When a broadcast bounces,
`EmailDeliveryActionService.ProcessDeliveryEventAsync` clears the five per-address opt-in
booleans. Committee forwarding never reads those booleans: both forwarding paths select
`Members.Where(m => m.ReceivesForwardedEmail)` and take `EmailAddresses.First(...)`
(`Web/Services/CommitteeMailPoller.cs:248`, `Web/Controllers/CommitteeController.cs:838`).
A hard-bounced address therefore receives every forwarded message forever. The same is true of
notification escalation digests (`Web/Services/NotificationEscalationRunner.cs:341`), and would be
true of any future mail type.

Committee forwards are **grouped sends** (`GroupRecipients = true` at
`CommitteeMailPoller.cs:504` and `CommitteeController.cs:971`). The grouped branch of
`EmailJobProcessor` builds the message with no footer, no token, and no `List-Unsubscribe`
headers - that code lives only in the per-recipient branch. So forwards have no unsubscribe
mechanism of any kind, by construction. There is nothing for a recipient or a mailbox provider to
use to make us stop.

**2. The unsubscribe path has no recovery when it fails.** On 2026-07-02 and 2026-07-10, eleven
consecutive `GET api/email/preferences` requests returned 400 within a minute of a broadcast
being sent. The page dead-ended on "The link may be invalid or expired." A resident in that
position reports spam instead, which is the exact reputation damage this work exists to prevent.

### What we know about the 400, and what we do not

Established:

- The same token that failed eleven times on 2026-07-10 validated successfully on 2026-08-06.
  So the token was valid, and the key that generated it is the key in production now.
- The key did not change (app-setting writes are ARM operations; the activity log is empty since
  2026-05-10). The code did not change (nothing in the token path has moved since 2026-04-05).
- Single worker, and the same `AppRoleInstance` served both the send and the rejections.

Given identical code, key, and token, the server cannot return 400 then and 200 now. The request
that arrived in July did not carry the token that is in the email. Something between the email and
the server altered it, and is no longer doing so.

**We do not know what.** This is a well-documented failure class:

- Security gateway URL rewriting (Microsoft Defender Safe Links, Proofpoint URL Defense, Cisco,
  Mimecast). Filed against NextAuth #1840, FusionAuth #629, Ghost #12347, nhost #189, Supabase.
- Quoted-printable soft line breaks at 76 characters splitting long URLs, with clients dropping the
  wrapped tail. Filed against Swiftmailer #72, Nodemailer #1275, Apple Mail.
- Unsubscribe-specific interception: Cisco graymail Safe Unsubscribe extracts the unsubscribe URI
  and performs the unsubscribe through a cloud service on the user's behalf.
- Postmark found roughly 1 in 1,000 of their unsubscribe URLs violated RFC header length limits,
  with Microsoft rejecting such messages outright. Their fix was to stop encoding data in the URL
  and reference backend-stored data with a compact identifier.

Our token is long enough to be exposed to all of this: payload `{guid}|{email}|{unix}` is ~73
bytes, plus a 12-byte nonce and 16-byte tag, giving ~135 characters of base64url, a body link near
190 characters and a `List-Unsubscribe` header near 200.

The design below does not depend on identifying the cause. It makes the failure diagnosable, makes
the link less likely to be mangled, and makes the failure recoverable without an account.

## Design

### Credential shapes

Three shapes converge on one payload. `UnsubscribeTokenPayload` (home id, address, issued) stays
the single currency; a resolver acquires it from any of:

| Shape | Source | Status |
|---|---|---|
| `?token=<~135 char AES-GCM>` | emails already sent | legacy, accept only |
| `/u/{id}` (16 random bytes, base64url) | new sends | primary |
| address + typed code | footer of new sends | fallback |

Discrimination is by URL shape and parameter name, never by sniffing the value. The legacy SPA
route `/email-preferences?token=` and the legacy API parameter keep working untouched.

The invariant "which home and which address is this credential for" is defined once, in the
resolver. Three acquirers, one answer.

### Why a short link

A stored short id puts the whole link near 35 characters against today's 190, which sidesteps
line-wrap truncation and most rewriting length problems. Cost is one Cosmos write per recipient per
send; at a couple of hundred recipients and a handful of broadcasts a year this is negligible.

Generate 16 random bytes, render base64url, `Add` with the id as the document id, treat a 409 as a
collision and regenerate. Container TTL of ~400 days so it prunes itself and the link lifetime
matches legacy expiry.

### Why a typeable code

If the link is mangled, a code is not, because it is not a link. The footer carries both:

```
Manage your email preferences: cohad.org/u/K7F29QMX
Or go to cohad.org/unsubscribe and enter your address with code K7F2-9QMX
```

The code is a truncated keyed HMAC over the normalized address - derived, not stored, so no
per-send writes. The page asks for address and code, looks the address up via
`IHomeRepository.GetByEmailAsync`, recomputes, and compares in constant time. ~40 bits with
per-address and per-IP rate limiting and lockout. It authorizes preference changes for that one
address only, exactly like the token today. Revocable by rotating its key.

Typing your own address on an unsubscribe page is acceptable friction. Registering an account and
waiting for admin approval is not, which is why signing in is a footnote and not a recovery step.

### Recovery hierarchy

1. The short link. Primary.
2. The code. Instant, self-service, no account, immune to link mangling.
3. A mailto, always visible. For people who no longer have the email and so cannot prove address
   control. Admin-mediated, and rare by construction. An anonymous request endpoint was designed
   for this slot and dropped in Part 2; the mailto serves the same people at no cost.
4. "If you already have an account you can also manage this under My Info." A note, not a step.
   (`myinfo.component.html:19` renders `app-edit-home`, which already exposes every opt-in.)

The abuse trade-off resolves cleanly: the instant paths require possession of the email, so they
cannot be used against a neighbour. The path that requires only knowing an address goes through an
admin.

### Error state (required)

Nobody will think to try the code unaided. On any credential failure the page must present the
input inline, not link elsewhere:

> **That link didn't work.** Some email providers alter links in transit, so this can happen even
> when nothing is wrong with your subscription.
>
> **Your email also contains a short code.** Look for "code" near the bottom of the message and
> enter it here, along with your email address.
> `[ your@email.com ] [ ____-____ ]  [ Continue ]`
>
> Don't have the email any more? Write to board@cohad.org.

## Work items

### Part 1: Make the next failure diagnose itself - **implemented**

`UnsubscribeController` returned `BadRequest` with no logging, and the app-wide log filter is
`Warning`, so nothing below it is exported. Five distinct conditions in `ValidateToken` collapsed
into a bare `null`.

- `ValidateToken` returns `UnsubscribeTokenResult` carrying a failure reason: `NotConfigured`,
  `Missing`, `MalformedBase64`, `TooShort`, `DecryptFailed`, `MalformedPayload`, `Expired`,
  `IssuedInFuture`. An out-of-range timestamp maps to `MalformedPayload` rather than throwing.
- **An authenticated payload carrying an empty email is rejected.** `GenerateToken` refuses one, but
  validation has to as well: `{guid}||{unix}` parses cleanly, and an empty address normalises to
  `""` in `FindMatchingEmailAddresses`, which then matches every blank-address record on the home.
  Anyone able to mint a payload - and the legacy key is treated as untrusted below - could otherwise
  read and clear those records' preferences. This is the one authorisation change in Part 1.
- **Every** rejection logs at **Warning**, with the credential type, the reason, the token's
  `length` as its own structured property, and its first and last four characters - only when the
  token is at least 32 characters, so a short credential is never mostly disclosed. Never the token
  itself; it is a bearer credential.
- The `List-Unsubscribe` body check on the one-click endpoint logs the same way. It returns 400 on
  the RFC 8058 path mailbox providers drive, and used to do so silently, which would have left a run
  of failures there exactly as undiagnosable as the original incident. Only the shape of the
  supplied value is recorded - it is attacker-controlled.
- **No reason is demoted below Warning.** An earlier revision logged the "no token supplied" case at
  Debug to blunt anonymous flooding. That was wrong: ASP.NET Core binds an empty `?token=` to null
  exactly like an absent parameter (`ConvertEmptyStringToNull` defaults to true), so the carve-out
  silenced the stripped-link signal this work exists to capture, and the unit test that "locked" it
  passed only by calling the action directly and bypassing model binding. It also failed to stop the
  flooding it was written for, since a garbage token still logs at Warning.
- **Rejections after a valid credential are logged too** - home not found, address no longer on the
  home, unknown category, missing body, retries exhausted. These returned 4xx silently, which left
  the most confusing case invisible: the token is valid, but the SPA renders every failure as "the
  link may be invalid or expired", so the resident dead-ends while the log shows only an acceptance.
  An operator reading that would conclude the request succeeded. Route- and body-derived values are
  attacker-controlled, so only the classification is recorded, never the value.
- **The logging lives in middleware, not in the action** (`UnsubscribeDiagnosticsMiddleware`). This
  is the single most important structural decision here, and it was reached the hard way: four
  separate defects came from logging inside the action body, because rejections are produced at
  three different layers and only the outermost sees them all.
  - The automatic **400** for an unparseable body comes from `[ApiController]`'s model-state filter,
    before the action runs. The action's own `dto == null` guard is unreachable over HTTP.
  - The **415** for a wrong content type is produced by **routing**, which matches a synthetic
    "415 HTTP Unsupported Media Type" endpoint - the MVC filter pipeline never runs at all. An
    `IAlwaysRunResultFilter` catches the model-state 400 but *not* this, which is why a filter was
    not enough and middleware was.
  - Two branches tried to distinguish an absent parameter from an emptied one *after* model binding
    had collapsed both to null (`ConvertEmptyStringToNull` defaults true for query **and** form
    values). Where that distinction matters it is now taken from `Request.Form`, which still knows.
  - The action records a classified reason via `UnsubscribeDiagnostics.Record`; the middleware
    decides whether it is logged and how. One place, one message shape per kind.
  - `{Operation}` is derived from the **path**, never from the MVC action name, because routing
    rejects some requests before choosing an action - an action-name label would give one endpoint
    two values and an operator filtering on either would silently miss half an incident. The
    acceptance log uses the same helper for the same reason.
  - The middleware observes **4xx only**. `UseExceptionHandler` is registered before `UseRouting`
    and therefore outside it, so a 5xx unwinds past without reaching it; the global handler already
    logs those with a stack trace. One consequence worth knowing: an unsubscribe URL that matches no
    route at all is answered by the SPA fallback with **200** and index.html, so it is not a 4xx and
    is not logged here.
  - The middleware is gated on the selected action's controller type, falling back to two specific
    path prefixes when routing rejected before choosing an action. It must **not** match on the
    `/api/email` prefix: `EmailController` is `[Route("api/[controller]")]`, which resolves to the
    same prefix case-insensitively, so a prefix match would log authenticated email-admin failures
    here and let them consume this budget.
- **Tests for rejection behaviour go through a real MVC pipeline** (`UnsubscribeDiagnosticsPipelineTests`).
  Two tests that invoked the action directly passed green while production did the opposite; a test
  that bypasses model binding cannot observe what model binding erased, and one that never issues an
  HTTP request cannot observe a rejection produced before the action. The pipeline harness also
  caught a broken log template on its first run - a repeated `{WarningKind}` placeholder with only
  three arguments, which throws at render time and which no mock-logger assertion would have found.
- Flooding is bounded by volume instead of by reason. `UnsubscribeWarningBudget` caps rejection
  warnings at **100 per 24-hour window** - comfortably above the largest genuine run ever seen
  (eleven, on 2026-07-10) - and the last warning of a window announces that the rest are suppressed,
  so the silence is attributable rather than ambiguous. Details that matter:
  - It is a **fixed** window anchored on the first warning, not a sliding one. A sliding window
    would need per-warning timestamps to buy a tighter bound; the cost of the simpler scheme is that
    warnings straddling a boundary can reach twice the cap in one 24-hour span, which is irrelevant
    two orders of magnitude above any observed run.
  - Each **kind** has its own budget, split by how far the request got rather than by whether it
    was authenticated - nothing here is authenticated. `TokenRejection` covers any request that
    **carried a token** - whether it validated, and whether or not the request got far enough to
    read it - plus failures after one validated. `PreTokenRejection` covers rejections of tokenless
    requests. The split is on token presence, not on how far the request got: a provider changing
    its RFC 8058 body or its content type is turned away before the token is read, but those
    requests do carry one and are the regression worth catching, so they must not be billed to the
    stream an empty POST can flood. `UnsubscribeDiagnostics.ClassifyByTokenPresence` is the single
    rule, used by both the controller and the middleware.
    What that buys is that the cheapest flood - an empty POST needing no token at all - cannot
    silence the stream carrying the stripped-link evidence. It does **not** mean a valid credential
    was held: a junk-token flood can still displace genuine token rejections inside one window. The
    cap sits an order of magnitude above any observed run, and the exhaustion announcement names the
    kind, so the suppression is visible rather than silent.
  - A clock moving **backwards** reopens the window rather than suppressing until real time catches
    up, so an NTP correction after a forward excursion cannot blind the endpoint for days. The clock
    is read **inside** the lock: read outside, two threads racing at a window boundary can enter
    with timestamps microseconds apart, and the earlier one then takes the backwards-clock branch
    and zeroes a counter the later one had just spent.
  - In-memory and non-durable, deliberately: a blast-radius limit on log volume, not an accounting
    record.
  - The cap covers **logging only**. Enforcement is never skipped, so a suppressed rejection is
    still a rejection - `SuppressingWarningsNeverSuppressesTheRejectionItself` drives a real request
    past the cap and asserts it is still rejected.
- **Accepted risk:** the acceptance log (Information) is not budgeted. Anyone holding one valid
  token can loop the endpoint and emit an Information line plus a Cosmos read per request. It is not
  budgeted because acceptances are the legacy-redemption counter that decides when legacy support is
  retired, and capping them would corrupt that count. The exposure requires a valid token and is
  bounded in practice by the size of this association.
- Credential type is recorded on every resolution, success and failure, so legacy traffic is
  measurable - and it reflects whether a credential was actually **supplied** (`LegacyToken` vs
  `None`), meaning the parameter carried a value. This is deliberately **not** the same rule that
  selects the budget, and the difference is the whole point: the budget asks "did this look like it
  came from an unsubscribe link" and so keys on the parameter being *present*, because a stripped
  `?token=` is the evidence most worth protecting from a flood. The credential type asks "was a
  credential actually supplied", and the two answers diverge on exactly that input. Deriving one
  from the other logged a stripped link as a rejected legacy token with `Token length 0`, which
  would hold the retirement counter above zero for precisely the traffic this work exists to
  surface. Acceptances log at Information; `appsettings.json` raises
  `Logging:LogLevel:Web.Controllers.UnsubscribeController` to `Information` so they are exported.

This distinguishes a mangled link (`MalformedBase64` / `DecryptFailed` at short length) from a key
mismatch (`DecryptFailed` at full length) from clock skew, in one query.

**Query-string exposure.** The token rides in the query string, so it is worth being precise about
where it can end up.

- **Backend: covered.** The OpenTelemetry ASP.NET Core instrumentation redacts every query value by
  default (`token=Redacted`), verified against `RedactionHelper` in the pinned 1.15.0 package. This
  can be disabled by `OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION`; do not set
  it. Our own rejection logging never records the token either - only its length and, above 32
  characters, its sanitised ends.
- **Browser: a known, unclosed gap.** The Application Insights JS SDK records the full request URL
  on telemetry this app does not raise by hand - Ajax/fetch dependency items, and the SDK's own
  internal diagnostics - and `trackPageView`'s existing query-stripping does not cover them. So a
  live token can reach the shared Application Insights resource from the browser and stay there for
  the resource's retention period.

  This was **deliberately taken out of Part 1** rather than shipped. An attempt at a browser-side
  redaction initializer went through six review rounds and produced seven distinct credential leaks,
  each from asserting a shape the SDK does not have or a regex behaviour that was never executed:
  a URL-recognition gate that returned false for the relative URLs this app issues, a query parser
  that gave up at the first unparseable pair, redaction of two `ExceptionData` fields the SDK never
  emits, a V8-only stack sentinel, and so on. It was not converging, and it is not Part 1's problem
  to solve.

  **Part 2 was expected to remove the exposure at its source. It did not, and the reason is worth
  recording, because the expectation was wrong in both directions.** The claim was that moving the
  credential from `?token=` into the `/u/{id}` path would stop the SDK recording it as a query
  value. What review of the implemented change established:

  - **The page view got worse before it got better.** `ApplicationInsightsService` strips the query
    and fragment from the tracked URI, so the legacy `?token=` was *already* protected there. A path
    segment is not stripped, so `/u/{id}` was published verbatim - and `location.replaceState` in
    the component cannot help, because the router URL is read before the component initialises.
    Fixed in PR 2a by redacting the `/u/{id}` segment where the page view is tracked.
  - **The SPA's own API call still carries the credential as a query value**, because the page has
    to send it somewhere, and that is a fetch/XHR dependency whose full URL the SDK records. This is
    unchanged from the legacy scheme, which sent `?token=` from the same place - so it is the
    pre-existing gap this document already describes, not a regression. Closing it means moving the
    credential to a request header, which telemetry does not record. That is a change to the API
    contract rather than the link format, so it is tracked on its own rather than folded in.

  The lesson generalises: "the credential is in the path now" says nothing about where the page
  subsequently *sends* it. A credential's exposure is the union of every place it travels, and the
  emitted link is only the first.

  If a broader browser-side rule is still wanted, it should be built against executed evidence -
  every field name read off the SDK source, every transform run against a table of real inputs - and
  reviewed on its own, not folded into another change.

  Until then, treat the browser half as uncovered: do not paste production telemetry URLs into
  tickets, and prefer rotating the signing key over relying on redaction if a token is known to have
  been exposed.

### Part 1 follow-up: a save that did not persist no longer reports success - **implemented**

`WithOptimisticRetry` caught every exception from the resident upsert, logged it at Error, and
returned the success response anyway. The per-address opt-in booleans live on `Resident` documents,
so the opt-out was not stored while the endpoint answered "Successfully unsubscribed" - and a
mailbox provider driving RFC 8058 one-click records that as honoured and stops offering the
control, leaving the resident with no way to make the mail stop and no sign anything went wrong.

Held out of Part 1 because it is a behavioural change rather than a diagnostic one.

- The resident write is now part of the save. A lost race retries the whole read-modify-write -
  re-reading and re-applying is what makes an already-written home safe to write again - and any
  other fault surfaces as a 500, which the SPA already renders as "Please try again" rather than as
  an expired link.
- The failure is logged at Error naming the home and the redacted address before it is rethrown.
  The global handler logs the fault itself, so this is not the only trace - it is the only one that
  says *which record* to repair, since the path it logs carries no identifier. Home plus address is
  enough: comparing the two copies of that address shows exactly which flags failed to land, so the
  category never has to reach this layer. It is deliberately **not** filtered on
  `OperationCanceledException`, against the usual repo rule - that rule protects a
  `BackgroundService` from logging shutdown as failure, whereas here it would only discard the log
  for `CosmosOperationCanceledException`, which derives from it. These repositories pass no
  cancellation token so that is not the common fault, but an abandoned save leaves the same split
  state as any other, and silence is what this line exists to prevent.
- The two containers have no transaction between them, so a fault after the first write still
  leaves a partial one - and a partial write is not a partial opt-out: `GetAllEmailsMatchingFilter`
  includes an address if *either* copy still opts in, so mail keeps flowing until both are written.
  That state is recoverable and findable; reporting success for it was neither.
- A failed save does not spend the warning budget, and a test now drives a full window of them
  followed by a genuine rejection to prove it - a Cosmos outage must not silence the mangled-link
  evidence at the moment the endpoint is loudest. Note *which* mechanism does that work: the failure
  is thrown, so it unwinds past `await _next` and the middleware's post-processing never runs. The
  documented `>= 500` carve-out is the second line of defence, for a 5xx *returned* as a result, and
  no unsubscribe path produces one of those - it remains unexercised.

**Closed, not open: sanitising the other log sites that render addresses.** Investigated and
deliberately dropped ([PR #286](https://github.com/bill-long/COHAD-archive/pull/286), closed
unmerged): O365 and Postmark own email security here, the residual log-forging risk carries no
privilege gain, and App Insights stores each entry as a discrete item so the forged line largely
does not land. Do not reopen it.

**Still open: residents have no optimistic concurrency.** `Resident` carries no `ETag` and
`CosmosResidentRepository.UpsertAsync` sends no precondition, so two writers who loaded the same
resident silently last-writer-wins - an opt-out saved during a concurrent Manage Homes save is
still lost, with a 200 and no log. The retry above is uniform over both containers, so the control
flow is ready for it, but closing the gap means giving `Resident` an ETag on every read and write
path (Cosmos *and* Mock, per the repository conventions) and deciding what each existing caller
does with a 409. That is its own change; it is not what "the save no longer reports false success"
covers.

**Still open: the home is written even when nothing on it changed.** `WithOptimisticRetry` upserts
the home unconditionally, including when the matched address lives only on a resident. That rotates
the ETag-guarded home document and can hand a concurrent admin edit a spurious 409. The three-
attempt retry around it is unchanged by this work - it predates it - but the honest failure does
widen the exposure in one case: a resident write that fails *deterministically* now 500s instead of
returning 200, and a mailbox provider retrying one-click will re-run the cycle, rotating the home's
ETag each time. Skipping the write when no home-level address matched fixes both, and is small, but
it changes which documents an unsubscribe touches and several existing tests assert the current
behaviour, so it is deliberately not folded into a fix about reporting failure honestly.

**Fixed since, separately: the same swallow in `HomeController.Update`.** It was *worse* than the
case above - it returned 200 **and** fell through to write an audit entry recording "Updated home
information." for creates, updates and deletes that never landed, so the one record an operator
would consult contradicted the data. The resident block now logs, records a *qualified* audit entry
("Update partially applied: the home record was saved, resident changes failed."), and rethrows. It
names only what is certainly true: the home document was written, so it is named, but what the
payload *changed* is not, because the client sends the whole home on every save and a residents-only
edit carries the existing email and phone unchanged. The entry is qualified rather
than dropped because the home's own email/phone write has already committed by then: writing
nothing would leave a real change to a published address with no record of who made it.

Making that failure reach the admin needed a second change, and finding out why is the useful part.
The API returning 500 changed nothing on screen: `HomeService` converts any HTTP failure into
`of(false)` rather than an error notification, and every caller in `edit-home.component.ts` passes a
hardcoded `true` onward with an `error` handler that can never fire. The failure is now reported
from `HomeService` itself - the single point that already knows - and **no caller's control flow was
touched**. That restraint is the design, not laziness: the components mutate one shared `homeCopy`
optimistically and have no rollback, so the current behaviour of closing the editor and re-syncing
from the server is what keeps unsaved edits from being re-submitted by a later, unrelated save.
Keeping the user on the page with a failed save - the obvious "better" fix - creates a data-loss
path where a failed resident deletion is silently committed by the next successful save. Any future
inline error state here has to bring a rollback model with it.

The doc previously flagged that this `catch (Exception ex)` does not exclude
`OperationCanceledException`, against the repo checklist. It still does not, now deliberately and
for a reason specific to this call site rather than inherited from the unsubscribe one: these
repositories accept no `CancellationToken`, so an abandoned request cannot surface here at all, and
the only realistic source is `CosmosOperationCanceledException` - which derives from it and *is* a
genuine half-applied write, exactly what the audit entry exists to record.

Two consequences are accepted rather than solved, and both are tracked:

- The admin's typed edits are still lost on failure (as they always were, just silently). Closing
  the editor is what discards the mutated `homeCopy`, so keeping the user on the page needs a
  rollback model first - [#284](https://github.com/bill-long/COHAD-archive/issues/284).
- The failure path dispatches `LoadAllHomes` but not `LoadDirectory`, so the directory view can lag
  until the next refresh - [#285](https://github.com/bill-long/COHAD-archive/issues/285).

**Fixed in passing: `HomeService`'s constructor subscription was not fault-tolerant.** `LoadAllHomes`
is handled by a `switchMap` over the dispatcher, and the error callback lived on the *outer*
subscription, so the first failing `GET api/home` terminated it permanently - the store was set to
an empty home list and no later dispatch was served until a page reload. It predates this change,
but the change makes it easy to reach: the failure paths dispatch `LoadAllHomes` to re-sync, and
they run precisely when the API is already failing. The error is now caught inside the `switchMap`,
so the outer stream survives; a test drives one failing reload followed by a successful one.

### Part 2: Recovery paths

Three pieces in two PRs. A fourth was proposed and dropped; see below.

**PR 2a - short link.** `UnsubscribeLink` and its repository pair, the credential resolver, minting
one row per recipient **at job creation in `EmailController`** (the processor only reads the stamped
id - three review rounds established that issuance cannot live safely inside the send loop, whose
ordering carries the attempt-budget, webhook-merge and already-Sent-skip invariants), the `/u/{id}`
route, and the diagnostics wiring for a second credential shape. Ships with the legacy cutover it forces
(`LegacySigningKey`, the cutover-date rejection, `GenerateToken` losing its production callers).

**This PR changes the credential shape and nothing else.** What an unsubscribe *writes* is not
touched here. That was briefly designed the other way - the resolver resolving an address to every
home carrying it, so an opt-out applied everywhere - and it is the wrong mechanism: address scoping
falls out of Part 3's suppression list for free, because a suppression is keyed on the address and
there is only ever one document. Building a multi-home walk in the resolver would be building the
same guarantee twice, in the harder place, and then deleting it.

**PR 2b - typed code and the inline error state.** The derived code, the `/unsubscribe` page, the
footer change, per-address and per-IP rate limiting with lockout, and the inline error state above.
These ship together deliberately: a code with no page that accepts it is undiscoverable, and an
inline code box in a release where no email carries a code is a worse dead end than the message it
replaces.

**Skipped, 2026-08-09 - not convinced it is needed.** The case that settled it: the typed code can
only ever rescue emails that carry a code, which is sends after 2b ships - and those same sends
carry the short link, the shape 2a built specifically to survive mangling. So the code insures the
residual failure rate of the thing designed to eliminate the failure, and that rate is unobserved
(2a went live the same day). The mangling-prone ~135-character legacy tokens still sitting in
inboxes can never use the code page, because those emails carry no code line. Meanwhile 2b's most
expensive piece - per-address/per-IP rate limiting with lockout, built from scratch on an anonymous
endpoint - is also its most review-prone. Part 1's diagnostics guarantee that if short links do
still fail it shows up in the logs (a stripped `?u=` or a `LinkNotFound` run at Warning); revisit
on that evidence, not before. The open multi-home question for the code path is moot until then.
The inline error state's mailto improvement was not commissioned either.

**One guard that survives the change of mechanism.** Part 1 rejects a blank address inside
`ValidateToken`, but the short-link and code acquirers never call it. The check belongs in the
resolver, where all three shapes converge; the legacy copy stays where it is so a malformed payload
is still classified as malformed rather than as an empty address.

#### Dropped: `POST api/email/unsubscribe-request`

An anonymous request endpoint was proposed here, raising an Administrators notification that
escalation would email. It is dropped in favour of recovery item 4, the always-visible mailto, which
already serves the same people at no cost.

The endpoint needed a notification type, frontend rendering for it, and a deep link to an admin page
that does not exist - nothing in Manage Homes acts on "this address asked to be removed" - plus
enumeration-safe response shaping and its own share of the rate limiter. It bought automation of a
path this design already calls rare by construction, for people who no longer hold the email. It was
also the only entry point in the set requiring no possession of the email, so it carried the abuse
profile of the whole feature while serving its smallest audience. Revisit if the logs ever show
people asking for it.

### Part 3: Suppression list

- `EmailSuppression` keyed on normalized address: reason (`HardBounce`, `SpamComplaint`,
  `ResidentRequest`, `AdminAction`, `ProviderUnsubscribe`), consecutive failure count, first and
  last seen, causing job, `ClearedUtc` / `ClearedBy`.
- **The record has to explain itself, because it is going on screen.** An admin looking at a
  suppressed address needs to know when it happened and why without reading logs, so the record
  carries the answer rather than leaving it to be reconstructed:
  - `SuppressedUtc` and the first/last-seen pair above, so "when" is unambiguous for both a
    one-off and a repeated failure.
  - `SuppressedBy` - who or what caused it: `system:delivery-event`, the credential type for a
    link-driven unsubscribe (which is also what Part 4 audits), `postmark:subscription-change`
    for a provider-layer unsubscribe, or the admin's user id.
  - The provider's own diagnostic for a bounce or complaint - Postmark's type and description -
    because "why" for a hard bounce is the provider's text, and paraphrasing it into one of four
    reason codes throws away the part that tells an admin whether the address is a typo or a
    mailbox that has closed.
  - `ClearedUtc` / `ClearedBy` for the same reasons, so an address that was suppressed and
    restored reads as a history rather than as an absence.
- **Surfacing it is in scope here, not a follow-up.** A suppression that only a Cosmos query can
  explain rebuilds the original problem one layer down: the bounce is recorded and still nobody
  hears about it. At minimum the address's suppression state and the fields above appear where an
  admin already looks at an address, and the email job detail page explains a `Suppressed`
  recipient in place rather than just labelling it.
- **A suppression is all-mail and carries no category.** It answers "does this address receive
  anything", not "which lists is this address on". Per-category choice stays where it is, in the
  five opt-in booleans, and the two are evaluated independently at the single enforcement point:
  an address is mailed when it is not suppressed **and** it opts in to the job's category.
- **The invariant: only a human editing preferences writes the opt-in booleans.** Everything else
  that wants mail to stop writes a suppression. This is a deliberate reversal of what the code does
  today, and it has two writers, which is why it is stated once here rather than twice below:
  - the **unsubscribe link** - the RFC 8058 one-click endpoint writes a suppression rather than
    clearing the category boolean it clears today. A recipient who clicks Unsubscribe in Gmail is
    asking for the mail to stop, and making it stop is the entire subject of this document.
  - **provider feedback** - `EmailDeliveryActionService` writes a suppression on a hard bounce or a
    spam complaint rather than clearing all five booleans.
  - **provider subscription changes** - Postmark's SubscriptionChange webhook (handled in
    `PostmarkWebhookController`, mutation in the same `EmailDeliveryActionService`) writes a
    `ProviderUnsubscribe` suppression when an address lands on a Postmark stream suppression
    list through Postmark's own mechanisms - a mail client's native Unsubscribe button on the
    broadcast stream's Postmark-managed List-Unsubscribe, or a manual suppression in the Postmark
    dashboard. Bounce- and complaint-origin subscription changes are skipped here because the
    Bounce/SpamComplaint webhooks own those reasons. A reactivation (`SuppressSending: false`)
    clears only a `ProviderUnsubscribe` record, enforced at write time by `onlyIfReason`.

  Both do the same wrong thing now: they overwrite the resident's stated preferences in order to
  record a fact that is not a preference, scatter it across every home carrying the address, and
  leave nothing to restore from - only counts reach the audit log today. A suppression row records
  it once, keyed on the address, and the preferences survive underneath it intact. That the two
  writers converge on one mechanism is most of the value: a bounce and a click mean the same thing
  to the send path, and today they are two different mutations of the same five fields.
- **Settled (2026-08-09): the preferences page keeps writing the booleans**, and only the
  unsubscribe *action* writes a suppression - a human deliberately choosing granular settings is
  exactly the writer the invariant protects, and an all-mail suppression cannot express "Garden
  Club yes, Board no". Two additions came with the decision:
  - The preferences GET reports an active suppression (`Suppressed`, `SuppressedUtc`, `Reason`,
    `CanResume`), because checkboxes whose changes silently have no effect while mail stays
    stopped are the same class of trap as the false "Successfully unsubscribed" Part 1 removed.
  - **Residents can lift their own `ResidentRequest` suppression** ("Resume receiving email",
    `POST api/email/preferences/resume`, credential-authorised). Undoing your own unsubscribe is
    symmetrical with making it - the same possession of the email authorises both - but a bounce
    or complaint record is evidence about deliverability, and lifting that stays admin-only.
    `ClearedBy` records `resident:{credentialType}`, the same provenance rule as `SuppressedBy`.
- **Single enforcement point:** the `pendingRecipients` filter at
  `Web/Services/EmailJobProcessor.cs:870`. It sits above both the grouped and per-recipient
  branches, so one rule covers broadcasts, committee forwards, and escalation digests. Any design
  that enforced this through a per-recipient footer or token would miss forwards entirely.
- Skipped recipients get a `Suppressed` status rather than vanishing, so the job detail page shows
  who was dropped and why.
- Forwarding stops using `EmailAddresses.First(...)` and prefers a deliverable address, skipping the
  member only when all of theirs are suppressed.
- When a forwarding member is suppressed, notify that committee's moderators.
  `INotificationRecipientResolver` already maps `committee:{id}` to exactly the people who can act.
  A bounce nobody hears about is the original failure mode. **Deferred out of PR-3b by decision
  (2026-08-09)** - the notification's resolution lifecycle (it must clear when the condition clears
  via paths unrelated to forwarding, while escalation fires on a timer) is a follow-up in its own
  right; see the PR-3b implementation notes. Forwarding still correctly *stops* mail to a fully
  suppressed member; only the proactive alert waits.
- `EmailDeliveryActionService` writes a suppression instead of clearing all five opt-in booleans,
  so preferences survive and are restorable. Today they are destroyed with no record of their prior
  values - only counts reach the audit log.
- Suppress on hard bounce and spam complaint immediately. Soft bounces (Postmark `Transient` /
  `SoftBounce`, currently mapped to `Deferred`) may suppress after N in a window; that is what the
  count field is for. **Settled: no soft-bounce rule ships until soft-bouncing addresses are an
  observed problem** - the field the future rule needs exists (it counts every piece of hard
  evidence), and nothing acts on the number today.

#### Implementation notes - PR-3a (backend, implemented)

The record, both writers, the enforcement point, the resident resume, and the admin API
(`api/email-suppressions`: list / create-as-`AdminAction` / clear, Administrator-only). Decisions
made in the writing, each where the doc above was silent:

- **Document id is SHA-256 hex of the normalized address** (`EmailSuppression.MakeId`), address
  kept as a field: email local parts may legally contain `/ \ ? #`, which Cosmos forbids in ids.
  Normalization is defined once (`NormalizeAddress`, delegating to `UserEmailHelpers.NormalizeEmail`).
- **One document per address, forever.** Repeat evidence on an active record: count++, `LastSeen`
  advances, the diagnostic refreshes when the new evidence carries one, and the original
  `Reason`/`SuppressedUtc`/`SuppressedBy` stay - "why is this suppressed" is still the first event.
  Re-suppression after a clear: a new episode (`Reason`/`SuppressedBy`/`SuppressedUtc` describe the
  new event, `Cleared*` reset) with `FirstSeenUtc` and the count preserved. Episode history beyond
  that lives in the audit log.
- **Enforcement is a marking pass, not a filter change**: `EmailJobProcessor.ApplySuppressions`
  flips still-sendable recipients to `Suppressed` - stamping `SuppressedUtc` and the reason on the
  recipient, so the job detail explains the skip with no join and the explanation survives a later
  clear - BEFORE the untouched `pendingRecipients` filter, so the grouped and per-recipient
  branches are covered by construction and the mock path runs the identical helper. One
  `GetActiveAsync` read per job run; a suppression written mid-run applies from the next run.
- **Fail-closed:** a throwing suppression read (a missing container) fails the job rather than
  sending unfiltered - the same philosophy as the UnsubscribeLink gate at job creation.
- **Terminal status is one rule** (`DeriveTerminalStatus`: no failures means Completed), replacing
  three inline copies. An all-suppressed job closes as Completed with 0 sent / 0 failed - nothing
  failed and nothing remained to do - and the new `SuppressedCount` on the job and its DTOs is
  what explains "Completed, sent 0" on screen. The stall watchdog's old all-Sent rule would have
  called that PartiallyCompleted.
- **The provider diagnostic is captured at webhook-parse time** as
  `EmailDeliveryEvent.ProviderDiagnostic` (Postmark `"{Type}: {Description}"`, SendGrid
  `"{event}: {reason}"`), not re-parsed out of `ProviderPayloadJson` at suppression time -
  provider shapes drift, and a parse failure then would silently lose the text the record exists
  to keep. `ProcessDeliveryEventAsync` now takes the whole event for the same reason.
- **One-click keeps its per-category route** (baked into every List-Unsubscribe header already in
  inboxes) and still rejects an unknown category - the segment is part of a URL we emitted, so an
  unrecognised value is mangling evidence. The response message is all-mail honest. The endpoint
  reads no home or resident at all now, which is the structural half of the invariant lock; the
  category-to-boolean setter (`TryGetCategorySetter`) is deleted outright, the `GenerateToken`
  removal-by-shape precedent.
- **Admin test-sends to a suppressed address are skipped too** - enforcement is blanket by
  design, and the job detail's in-place explanation is the mitigation for the surprised admin.
- One-click no longer emits `home-not-found` / `email-not-on-home` rejections (no home walk);
  both remain on the preferences GET/PUT.
- **Accepted, not fixed (review round): one-click writes the suppression without checking the
  directory.** The old path 404'd when the credential's home was gone or the address no longer on
  it; the new one suppresses any address a resolvable credential names. That is the design, not
  an oversight: possession of mail sent to the mailbox is the authority (RFC 8058 semantics -
  Postmark's own suppression works the same way), a suppression for an address no longer in the
  directory is a harmless no-op at the enforcement point, and the narrow residual - the same
  address string later attached to a *different* resident - is admin-recoverable and surfaced by
  3b's admin page. Re-adding a directory walk would re-couple the writer to the home model the
  whole part exists to decouple from.
- **From the same review round**, four hazards were closed before merge rather than shipped:
  - The **conflict merge adopts a server-side `Suppressed`**, and both send branches now re-check
    AFTER the attempt persist (the grouped branch builds its To list there too) - without that, an
    instance that learned of a suppression only via the merge would mail the address anyway and
    stamp it Sent, defeating the enforcement point from a path it never sees.
  - **Evidence idempotence** (`LastEvidenceKey` = the delivery event's deduplicated id): the
    delivery-action path is at-least-once (the event is marked processed only after the write),
    and without the key a replayed event inflates `ConsecutiveFailureCount` and duplicates audit
    entries - lying to the exact typo-vs-dead-mailbox decision the record exists to inform.
  - **Exhausted write races surface as `ConcurrencyConflictException` → 409**, preserving the old
    `WithOptimisticRetry` contract on one-click (and resume/admin), instead of a 500 that pages
    someone for expected contention. The Cosmos repository also maps Replace-on-deleted (404,
    item sub-status only) to the same exception, matching the Mock.
  - The enforcement map is built by **looping over normalized stored addresses, not
    `ToDictionary`** - hand-authored case-variant rows would otherwise throw on the duplicate key
    and fail every send job; and the resident resume's "only ResidentRequest may be lifted" rule
    moved **inside the service's write cycle** (`onlyIfReason`), because a controller-side check
    races the window between page load and click. The admin clear acts by document id
    (`ClearByIdAsync`) so a hand-authored row whose id doesn't match its own address is still
    clearable, and its audit entry is gated on the service reporting that THIS call performed the
    transition.

#### Implementation notes - PR-3b (forwarding preference + all frontend surfacing)

The forwarding deliverable-address preference and all frontend surfacing (job detail badge +
in-place explanation, the Manage > Suppressions page, read-only address chips, the
preferences-page banner and resume button, the `'Suppressed'` recipient-status value in the SPA's
models). Decisions made in the writing:

- **Recipient selection is hoisted into `CommitteeForwardJob.SelectForwardRecipients`** (the
  `ApplyOriginator` precedent), replacing the two character-identical LINQ copies in the poller and
  `ApproveHeldMessage`. Per member: first non-blank address not on the suppression list (compared
  via `EmailSuppression.NormalizeAddress`); all candidates suppressed → member excluded (their mail
  is stopped); no addresses at all → skipped silently, as before. Dedup by address stays
  case-insensitive-first-wins and runs *after* the per-member preference, so a member falling back
  to a shared second address still collapses into one send. Empty result keeps the existing
  handling: the poller logs and moves the message to Processed; the approve returns 400 ("no
  forwarding recipients with deliverable email addresses"), refusing before the held message is
  claimed so it stays retryable.
- **The suppression set is read once per poll cycle / per approve** through the shared
  `EmailSuppression.ActiveNormalizedAddressSet` helper (the same normalization/guard invariant the
  enforcement point's `ActiveByNormalizedAddress` uses, so the three sites can't drift). A throwing
  read fails closed rather than selecting with an empty set and silently preferring suppressed
  addresses, but only for *forwarding* - the approve 500s before claiming the held message, and the
  poller passes a `null` set through so that **holding of non-directory mail (spam/phishing
  quarantine) and processed-folder cleanup still run**, while any message that would be forwarded is
  left in its inbox for a later cycle. Only a committee that actually deferred a message that cycle
  is stamped `LastPollStatus` = `Forwarding deferred` (a committee with an empty inbox, or one whose
  mail was only held, still reads `Success`), so the management UI reflects the real per-committee
  impact rather than a blanket failure. The deferred messages forward on the next cycle once the
  store is readable.
- **The job detail explains a suppressed recipient from the recipient-stamped fields** (no join to
  the suppression record), so the explanation survives a later clear. The badge is muted, not
  failed-red: the skip is the system working. Suppressed counts as handled in the progress math,
  so an all-suppressed Completed job reads 100%, not stuck.
- **The editors' suppressed chip is read-only and admin-fetched.** No Clear button inside
  edit-resident or the home-contact dialog: they mutate a shared optimistic `homeCopy` with no
  rollback (the documented data-loss trap), so clearing lives only on Manage > Suppressions. The
  chip's data comes from a shared session cache (`SuppressedAddressesService`) that issues no
  request at all for non-Administrators - the list endpoint is Administrator-only and both editors
  are reachable via resident self-edit - and is refreshed after an admin create/clear so the chips
  never disagree with the list within a session.
- **Resume reloads rather than guessing:** the preferences page's Resume button POSTs and then
  re-GETs, so the banner always reflects server truth; a 400 (state changed underneath - e.g.
  cleared then re-suppressed by a bounce) also reloads, and a 409 reads as "try again".
- **MockData seeds a held message** (`MockHeldMessageRepository.SeedSampleData`) because nothing
  else in that environment can create one (only the Graph-backed poller holds mail). Taylor's
  *primary, directory-visible* address is ordered first so every "first address" consumer (the
  committee-member picker, the forwarding-status preview) surfaces the real address; the seeded
  suppression sits on her hidden second address (`taylor.old@cohad.local`). The suppression demo
  therefore lives in a Board *broadcast* (where the suppressed address is its own recipient and the
  job detail marks it `Suppressed`); the deliverable-address forwarding preference is covered by
  unit tests rather than by ordering a hidden address first in the seed.

**Deferred by decision (2026-08-09): the moderator in-app notification.** The doc's "notify that
committee's moderators when a forwarding member is suppressed" bullet is *not* in 3b. Two review
rounds established that the notification's lifecycle is genuinely hard: it must resolve when the
condition clears (address fixed, member removed from forwarding, resident deleted, suppression
cleared) - all paths unrelated to committee forwarding - while `NotificationEscalationService`
escalates aged unresolved notifications on a timer with no type filter. A forward-time
reconciliation cannot own that (no forward may ever run; escalation can fire on a fixed-but-not-yet
-reconciled record). Rather than ship a half-right escalating notification that can email a stale
"member undeliverable" alert, the alerting is deferred to a follow-up designed with its full
lifecycle in mind (options include excluding this type from email escalation, or having escalation
re-check the live condition). The suppression itself remains visible on the Manage > Suppressions
page and, once a member is dropped, the forward simply excludes them - so mail is correctly stopped;
only the proactive moderator *alert* waits. This is the same "defer the piece with the hard
lifecycle" precedent set below for `NotificationRecipientResolver`.

`NotificationRecipientResolver.ResolveUserEmailsAsync`'s own First-non-blank address
pick is a documented follow-up beyond 3b: enforcement already prevents any send to a suppressed
digest recipient, so changing digest recipient *identity* is orthogonal.

### Part 4: Audit every subscription-state change

`UnsubscribeController` has no `IAuditLogRepository`. One-click unsubscribe, the preferences PUT,
the request endpoint, and admin suppression clears all write entries using the existing
`EmailDeliveryActionService.RedactEmail` helper. Record which credential type was used.

### Part 5: Exercise one-click before trusting it

`POST api/email/unsubscribe/{category}` has never been invoked in production. It is the path
Gmail's UI uses, bypasses the SPA entirely, and carries the same long token, so it is exposed to
the same mangling. Drive it once with a real token.

## Legacy compatibility

Legacy tokens remain valid until 365 days after issue (`UnsubscribeTokenService.MaxTokenAge`).

- Move the existing key to **`UnsubscribeToken:LegacySigningKey`**, validation only. Nothing is ever
  generated with it again. The new scheme gets fresh secrets.
- **This is a rotation, not only a rename.** The production `UnsubscribeToken__SigningKey` leaked
  into a transcript on 2026-08-06, so the Part 2 cutover must set a fresh value for it and move the
  leaked one to `LegacySigningKey`. What that buys is bounded and worth stating plainly: the leaked
  key can still mint payloads that the legacy validator accepts, and it must, because the links
  already in people's inboxes were minted with it. The cutover-date rejection below is what
  contains it - a forged token has to claim an issue date before cutover, so the exposure expires
  on its own 365 days after cutover along with the genuine links.
- `UnsubscribeTokenService.ValidateToken` survives as the legacy acquirer. `GenerateToken` loses its
  production callers, with a test locking that.
- The legacy path additionally rejects tokens claiming to be issued after the cutover date. This
  does not stop a forger, who controls the timestamp, but keeps genuine traffic honest and the logs
  unambiguous.
- **The legacy key is treated as untrusted.** It stays on the deprecated validation-only path, with
  Part 4's auditing behind it, and must not be reused for the new scheme.

**Retirement.** Earliest safe removal is one year after the final send in the old format; record the
actual cutover date in a comment at the legacy acquirer. Better than the date is the signal: when
Part 1's credential-type counter shows legacy redemptions at zero and holding, removal is
evidence-based. If they never reach zero, we find out before breaking someone.

## Deployment requirements

Two new Cosmos containers, provisioned out of band like every other container here
(non-partitioned, `/NoPartitionKey`):

- `EmailSuppression` - **no TTL**, deliberately: a suppression is permanent until a human clears
  it, and a row that quietly expired would resume mail to an address that bounced or complained.
  Must exist before deploying PR-3a; a missing container is loud by design (the enforcement point
  fails the job rather than sending unfiltered, and the point read's sub-status narrowing keeps a
  missing container from reading as "not suppressed").
- `UnsubscribeLink` (set container TTL ~400 days) - **provisioned, live since 2026-08-09**

Cut-over note: links are stamped on recipients **when a job is created**, so a job already queued
when the new build deploys predates the convention and has no ids. The retry endpoint backfills
missing links (same seam as creation: synchronous, admin-visible, nothing sent on failure), which
covers the failed-then-retried case; for the queued case, deploy with the send queue empty - at
this association's send volume that is the normal state.

New configuration:

- `UnsubscribeToken:LegacySigningKey` - the existing production key, validation only. It leaked into
  a transcript on 2026-08-06, so moving it here is a rotation and not a rename: `SigningKey` gets a
  fresh value at the same time. See Legacy compatibility for what the rotation does and does not buy.
  **Done at the 2026-08-09 cutover.**
- A fresh key for the derived code. **Not needed - 2b is skipped.** Part 3 adds no configuration
  at all: the container name is hardcoded like every other, and there are no flags or intervals.

Mock implementations must be behaviourally identical to the Cosmos ones, including 409 on duplicate
id and ETag handling.

## Sequencing

Parts 1 and 2 first: small, they close the dead end, and they make the unexplained failure
self-reporting. Part 3 next as the substantial piece. Parts 4 and 5 alongside either.

Per the repo test policy, each new endpoint ships with its tests in the same PR: success,
authorization, validation, and rate limiting. Plus:

- A legacy token still resolves to the same payload after the new scheme ships.
- Short id and typed code resolve to an identical payload for the same address.
- A credential carrying a blank address is rejected by the resolver, for every shape, before any
  lookup runs.
- An unsubscribe writes a suppression and leaves every opt-in boolean untouched - the invariant in
  Part 3, locked once per writer (the one-click endpoint and the delivery-event path).
- All three write audit entries recording the credential type.
- A legacy token presented after legacy support is removed lands on the recovery page with the code
  input, not a bare error.
- Short-id collision on `Add` regenerates rather than throwing.
- Suppression is enforced for grouped sends, not only per-recipient sends.

## Addendum (2026-08-10): Postmark owns the List-Unsubscribe header on broadcast mail

Investigation of a blast recipient with no delivery events revealed a fact the design above did
not account for: the Postmark broadcast stream is configured with Postmark-managed unsubscribe
handling (`UnsubscribeHandlingType: "Postmark"`). On every message sent through that stream -
which is every blast category - Postmark **replaces** our `List-Unsubscribe` /
`List-Unsubscribe-Post` headers with its own hosted URL (`subscriptions.pstmrk.it/...`), verified
against a delivered message dump. Consequences:

- A mail-client unsubscribe button (Gmail, Apple Mail) on blast mail drives **Postmark's**
  suppression list, not ours. The address keeps its COHAD opt-in state, our send loop keeps
  handing Postmark messages for it, Postmark accepts the SMTP transaction (250 OK, recipient
  marked Sent) and silently drops the message: no delivery, no webhook, no Delivery indicator,
  forever. One real recipient was found in this state (see the Postmark broadcast suppression
  dump; deliberately not named here).
- **Part 5's premise is invalid for broadcast mail.** `POST api/email/unsubscribe/{category}` is
  not "the path Gmail's UI uses" for blasts and its zero production invocations are structural,
  not suspicious: the one-click endpoint is reachable only from fallback-transport mail and from
  pre-cutover headers still in old inboxes (the broadcast stream dates from 2026-04-30).
- The "baked into every List-Unsubscribe header already in inboxes" rationale for keeping the
  per-category one-click route now applies only to pre-cutover mail, though the route still
  costs nothing to keep.

**Decision: keep Postmark's unsubscribe handling** (not `Custom`). Postmark's deliverability
expertise protects sender reputation, their hosted unsubscribe works even when our site is down,
and they satisfy the Gmail/Yahoo one-click requirement on the stream that carries bulk mail. The
gap this creates - Postmark-layer unsubscribes invisible to COHAD - is closed by syncing their
suppressions back into ours: the SubscriptionChange webhook writes a `ProviderUnsubscribe`
suppression (implemented; issue #7), and periodic reconciliation against the suppression dump
catches anything suppressed while the webhook trigger was not configured (issue #9,
implemented - see below).

`ProviderUnsubscribe` is deliberately **not** resident-resumable, unlike `ResidentRequest`:
lifting only COHAD's record would resume "successful" sends that Postmark still silently drops,
because Postmark keeps its own suppression entry for the address. Clearing one is an admin
action that acts on BOTH systems: the Manage > Suppressions clear also calls Postmark's
suppression-delete API to reactivate the address on the stream suppression lists (issue #11,
implemented - see below), so the two-system clear is one action. This preserves the Part 3
invariant with one refinement: `IEmailSuppressionService` remains the single writer of COHAD
suppressions, and the Postmark sync becomes one more evidence source feeding it, exactly like
bounce and complaint webhooks.

#### Implementation notes - issue #9: periodic reconciliation against the suppression dump (implemented)

`PostmarkSuppressionSyncService` (hosted, self-disabling, same shape as `UserPurgeService`) runs
`PostmarkSuppressionSyncRunner` on an interval - daily by default, enabled in production via
`Postmark:SuppressionSync:Enabled`. The runner reads both streams' suppression dumps through
`PostmarkSuppressionClient` (`GET /message-streams/{stream}/suppressions/dump`, the
codebase's first Postmark HTTP client, authenticated with the same `Postmark:ServerToken` the
SMTP transport uses; named `PostmarkSuppressionDumpClient` until issue #11 gave it a second
endpoint) and records a COHAD suppression for any dumped address not already actively
suppressed here. Decisions made in the writing:

- **Additive only.** The runner never clears: a clear is a deliberate act, and an absent dump
  entry can also mean a reactivation the webhook already mirrored.
- **Idempotent per dumped address.** The evidence key is deterministic over (stream, normalized
  address) - `postmark:suppression-dump:{stream}:{address}` - so a rerun against an unchanged
  dump applies nothing: no count inflation, no duplicate audit entries, the same evidence-key
  discipline as the webhook writers. One deliberate consequence: an address cleared in COHAD
  while Postmark still holds its suppression entry (a hard-bounce clear whose manual dashboard
  step was skipped - a `ProviderUnsubscribe` clear reactivates provider-side BEFORE clearing,
  so it cannot reach this state) is re-suppressed by the next run, because Postmark still
  silently drops its mail - the re-suppression is the truthful state.
- **Reason mapping:** Postmark `ManualSuppression` -> `ProviderUnsubscribe`; `HardBounce` /
  `SpamComplaint` -> the matching COHAD reason; anything unrecognized -> `ProviderUnsubscribe`
  with a warning, because the address IS on Postmark's suppression list and "do not mail" is the
  only safe reading (Postmark's own reason text is preserved on the record's diagnostic).
- **Both streams, one record per address.** The broadcast and transactional dumps are both read;
  an address on both is recorded once (the first sighting wins, the second counts as
  already-suppressed). A failed dump read aborts the run for the next interval to retry; a
  failed record write for one address is logged and skipped without losing the rest of the dump.
- **The record's `SuppressedUtc` is Postmark's `CreatedAt` when the dump carries one, not the
  run time.** The dump entry
  carries when the address was actually suppressed provider-side, and `RecordAsync` takes it as
  an optional `eventUtc` used only for a new episode's "when the suppression began" - the admin
  reading SUPPRESSED on the Manage page learns when mail actually stopped, not when the
  reconciliation happened to notice. First/last-seen keep their meaning (when the evidence
  arrived here). Records written before this refinement keep their run-time stamp; the
  evidence-key no-op deliberately does not rewrite them (a clear-and-resync re-stamps from the
  dump).
- **No durable pacing state**, for the same reason as the user purge: the pass is idempotent and
  additive, so over-running is free and under-running only defers the same records.
- **MockData runs it end to end** against `MockPostmarkSuppressionDumpClient`, whose seeded dump
  entry (`postmark.unsubscribed@cohad.local`, an address with no COHAD suppression) is recorded
  by the first sync run, so the reconciler's provenance is exercisable on the Manage >
  Suppressions page without a Postmark account.
- The **reverse direction** - admin clear also reactivating the address in Postmark - was
  deliberately NOT in this change; it is issue #11, implemented below.

#### Implementation notes - issue #11: admin clear reactivates the address in Postmark (implemented)

Clearing a `ProviderUnsubscribe` suppression on Manage > Suppressions also deletes the
address's entry from Postmark's stream suppression lists
(`POST /message-streams/{stream}/suppressions/delete`, a second endpoint on the renamed
`PostmarkSuppressionClient`), so the two-system clear the addendum above documented as a manual
procedure is one action. Decisions made in the writing:

- **Both streams are targeted, concurrently** (`PostmarkReactivationService`, over
  `PostmarkOptions.GetConfiguredStreams()` - the same deduped stream list the reconciliation
  reads): the COHAD record does not store which stream suppressed the address, only the
  diagnostic text, and Postmark reports success ("Deleted") for an entry that does not exist,
  so over-targeting costs nothing. One stream's failure does not stop the other's attempt,
  and a provider timeout (`HttpClient` reports its own timeout as `TaskCanceledException`) is
  a failed stream, not a cancellation - only the caller's own cancellation propagates.
- **The provider reactivation runs FIRST; a failure fails the clear.** An earlier draft
  cleared COHAD first and surfaced a provider failure as a warning banner, relying on the
  reconciliation to bound the divergence - but the sync is opt-in
  (`Postmark:SuppressionSync:Enabled`), the ephemeral banner kept sprouting lifecycle bugs
  (wiped by the very toggle its text pointed at; overwritten by the next clear), and the
  "retry" affordance on a cleared row was really an unconditional clear that could lift a
  newer hard-bounce suppression. Provider-first makes the bad state unrepresentable: a
  `ProviderUnsubscribe` record can only read Cleared after Postmark confirmed the delete (or
  no provider is configured), so "cleared here, still dropped at the provider" never exists.
  A failed provider call answers 502 naming the address, the record stays in force, and
  clicking Clear again is the whole retry - the UnsubscribeLink send-gate philosophy. The
  reverse divergence (provider reactivated, then the local write loses its race) is safe:
  mail stays suppressed here, and the retried clear's provider delete is a no-op.
- **The clear acts on the episode the admin saw.** The UI sends the row's displayed
  `SuppressedUtc` (reset by every re-suppression, so it identifies the episode), the endpoint
  checks it against its own read, and `ClearByIdAsync` enforces it again at write time
  (`onlyIfSuppressedUtc`, the resident-resume `onlyIfReason` discipline). A record
  re-suppressed between page load and the write - whatever the new reason - answers 409
  instead of being lifted on stale information. The one asymmetric edge: if the provider
  delete succeeded but the write-time guard then refused, the provider-side change is real and
  gets its own audit line, and mail stays suppressed here - the safe direction.
- **A refused provider call names its cause.** The 502 carries the provider's own refusal
  text: "SpamComplaint suppressions cannot be deleted" is permanent (Postmark lets only the
  recipient lift a complaint entry - the COHAD record then rightly stays suppressed), and a
  generic "try again later" would send the admin in circles on it. Zero configured streams
  reports itself as a configuration problem for the same reason.
- **A stale dump snapshot cannot undo the clear.** The reconciliation could have fetched a
  stream dump moments before the delete, and its add-only write would silently re-suppress
  the just-cleared record with nothing left to lift it. The runner stamps when each stream's
  dump was fetched, and `RecordAsync` ignores snapshot evidence for a record cleared at or
  after that instant (`evidenceSnapshotUtc`). Keyed on the fetch time rather than the entry's
  own `CreatedAt` deliberately: a half-done manual clear (hard bounce cleared here, dashboard
  step skipped) must still be re-suppressed by the next run, whose fresher snapshot postdates
  that clear even though the entry's provider date does not.
- **Only failure is strict.** The delete response is read in the opposite lenience direction
  from the dump: an ambiguous answer (no entry for the address, unparseable body) reads as
  failure, because a false "reactivated" leaves the admin believing mail flows while Postmark
  still silently drops it - a false failure only fails a retryable clear.
- **Scope stays `ProviderUnsubscribe`, keyed on the record's current Reason.** Clearing a
  `HardBounce`/`SpamComplaint` suppression does not touch the provider, so the manual
  procedure remains for those: after clearing here, also reactivate the address on the
  stream's suppression list in the Postmark dashboard (deleting a hard-bounce entry
  reactivates the bounce - a deliverability decision this change deliberately keeps human), or
  the nightly sync re-suppresses the address on its next pass. Spam-complaint entries cannot
  be deleted at Postmark at all, by API or dashboard - only the recipient can undo one. Two
  accepted consequences of keying on Reason: an active record whose episode began under
  another reason keeps that reason even if a provider unsubscribe later arrives as repeat
  evidence (`RecordAsync` deliberately preserves the first event's "why"), so its clear does
  not touch the provider and the nightly sync truthfully re-suppresses it; and conversely the
  reactivation deletes the address's entry on BOTH streams whatever their provider-side
  reasons (the record does not store which stream or why - issue #11's own prescription), so
  it can also lift a hard-bounce entry the other stream held, which the next bounce webhook
  re-suppresses.
- **A deployment where Postmark does not carry the mail skips the provider call and clears
  normally** (`NotConfiguredPostmarkReactivationService`, registered on the
  `DisabledSpamClassifier` precedent when `Postmark:Enabled`, `UsePostmarkAsDefault`, or
  `ServerToken` says the send path is not Postmark's): in webhook-only mode, with Postmark
  disabled but a stale token left in settings, or with no Postmark at all, sends do not pass
  through Postmark's suppression filter, so there is no provider-side entry dropping mail and
  refusing the clear would block the admin over a false, unresolvable problem. MockData keeps
  the real service over the in-memory client, which needs no token.
- **MockData closes the loop end to end:** `MockPostmarkSuppressionClient.ReactivateAsync`
  deletes the seeded dump entry, so clearing the reconciler-recorded suppression prevents the
  next sync run from re-suppressing it - the same fight-free behavior the real provider gives.

**The header generation in `EmailJobProcessor` stays**, dormant on broadcast mail, because it is
the only RFC 8058 compliance on the non-Postmark fallback transport and on any category ever
rerouted off the broadcast stream. The body-footer short link is the COHAD-owned unsubscribe path
on every transport and the only email-borne writer of COHAD's own suppression and preference
records.
