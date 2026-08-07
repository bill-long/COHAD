# Email suppression, unsubscribe recovery, and short links

Design doc. Status: **Part 1 implemented** (see below); Parts 2-5 proposed.

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
3. Request removal. For people who no longer have the email and so cannot prove address control.
   Admin-mediated. Rare by construction.
4. A mailto, always visible.
5. "If you already have an account you can also manage this under My Info." A note, not a step.
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
> Don't have the email any more? [Request removal] or write to board@cohad.org.

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

  **Part 2 removes the exposure at its source.** Replacing `?token=<135 chars>` with `/u/{id}` puts
  the credential in a path segment rather than a query parameter, so the SDK stops recording it as a
  query value at all. Hardening a query-string redactor for a query string we plan to stop using is
  the wrong order of work. If a browser-side rule is still wanted after Part 2, it should be built
  against executed evidence - every field name read off the SDK source, every transform run against
  a table of real inputs - and reviewed on its own, not folded into a diagnostics change.

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

**Still open: other log sites render addresses unsanitised.** The Error line above sanitises the
address it logs, because storage accepts any value containing an '@' and a CR/LF saved into a
resident record would otherwise forge a second log entry. The same is true of the pre-existing sites
that log an address - `EmailDeliveryActionService`, `EmailJobProcessor`, `PostmarkEmailTransport`,
`NotificationEscalationRunner` - and of `CommitteeMailPoller`'s `{Sender}`, which is the strongest
case of the set because it comes from inbound external mail rather than from a directory record.
Most are below the production `Warning` filter, which limits but does not close it. Fixing them
means one shared helper rather than a private one per call site, so it is its own change.

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
("resident changes failed and may be partly applied"), and rethrows. The entry is qualified rather
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

Two consequences are accepted rather than solved: the admin's typed edits are still lost on failure
(as they always were, just silently), and the failure path dispatches `LoadAllHomes` but not
`LoadDirectory`, so the directory view can lag until the next refresh.

**Still open, and pre-existing: `HomeService`'s constructor subscription is not fault-tolerant.**
`LoadAllHomes` is handled by a `switchMap` over the dispatcher whose error callback lives on the
outer subscription, so the first failing `GET api/home` terminates it permanently - the store is set
to an empty home list and no later dispatch is ever served until a page reload. This predates the
change (the failure path already dispatched `LoadAllHomes`), but making failures visible makes it
easier to reach: the snackbar invites a retry during exactly the outage that kills the subscription.
The fix is to move the error handling inside the `switchMap` so the outer stream survives.

### Part 2: Recovery paths

- Short link generation and the `/u/{id}` route.
- Derived code, the `/unsubscribe` page, and the inline error state above.
- `[AllowAnonymous] POST api/email/unsubscribe-request`: creates a request record and raises an
  `INotificationService` notification to the Administrators audience, which the existing
  `NotificationEscalationRunner` turns into an emailed digest with a deep link. It does **not**
  apply the opt-out directly. Identical generic response whether or not the address exists, so it
  cannot enumerate the directory. Rate limited per address and per IP. Note field capped and
  stripped.

### Part 3: Suppression list

- `EmailSuppression` keyed on normalized address: reason (`HardBounce`, `SpamComplaint`,
  `ResidentRequest`, `AdminAction`), consecutive failure count, first and last seen, causing job,
  `ClearedUtc` / `ClearedBy`.
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
  A bounce nobody hears about is the original failure mode.
- `EmailDeliveryActionService` writes a suppression instead of clearing all five opt-in booleans,
  so preferences survive and are restorable. Today they are destroyed with no record of their prior
  values - only counts reach the audit log.
- Suppress on hard bounce and spam complaint immediately. Soft bounces (Postmark `Transient` /
  `SoftBounce`, currently mapped to `Deferred`) may suppress after N in a window; that is what the
  count field is for.

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

- `EmailSuppression`
- `UnsubscribeLink` (set container TTL ~400 days)

New configuration:

- `UnsubscribeToken:LegacySigningKey` - the existing key, validation only.
- A fresh key for the derived code.

Mock implementations must be behaviourally identical to the Cosmos ones, including 409 on duplicate
id and ETag handling.

## Sequencing

Parts 1 and 2 first: small, they close the dead end, and they make the unexplained failure
self-reporting. Part 3 next as the substantial piece. Parts 4 and 5 alongside either.

Per the repo test policy, each new endpoint ships with its tests in the same PR: success,
authorization, validation, rate limiting, and the enumeration-safety assertion on the request
endpoint. Plus:

- A legacy token still resolves to the same payload after the new scheme ships.
- Short id and typed code resolve to an identical payload for the same address.
- All three write audit entries recording the credential type.
- A legacy token presented after legacy support is removed lands on the recovery page with the code
  input, not a bare error.
- Short-id collision on `Add` regenerates rather than throwing.
- Suppression is enforced for grouped sends, not only per-recipient sends.
