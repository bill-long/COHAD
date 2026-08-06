# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project overview

COHAD (Canyon Oaks Homeowners Association Directory) is a .NET 10 ASP.NET Core backend + Angular 20 SPA frontend. See `COHAD.sln` for the full solution structure (Web, Web.UnitTests, Web.IntegrationTests).

## Commands

### Backend

```bash
dotnet build Web/Web.csproj
dotnet test Web.UnitTests/Web.UnitTests.csproj
RUN_COSMOS_INTEGRATION_TESTS=1 dotnet test Web.IntegrationTests/Web.IntegrationTests.csproj
```

### Frontend

```bash
cd Web/ClientApp && npx ng build          # type-check (no lint target configured)
cd Web/ClientApp && npx ng test --no-watch --browsers=ChromeHeadless
```

### Running locally (dev mode)

```bash
# Terminal 1
cd Web/ClientApp && npx ng serve --host 127.0.0.1 --port 4200

# Terminal 2
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="https://127.0.0.1:5001" dotnet run --project Web/Web.csproj
```

Trust the dev certificate once: `dotnet dev-certs https --trust`. The backend proxies SPA requests to `http://127.0.0.1:4200`. Use `--host 127.0.0.1` (not the default) to avoid IPv4/IPv6 proxy mismatches.

### Mock data mode (no Cosmos DB or Azure AD B2C required)

```bash
# Terminal 1
cd Web/ClientApp && npm run start:mock

# Terminal 2 (from repo root — generates signing keys and runs the API)
./scripts/run-mock-data.sh api
```

Or run `./scripts/run-mock-data.sh` with no args and paste the printed one-liner. MockData serves **HTTPS** at **https://127.0.0.1:5001**; run `dotnet dev-certs https --trust` once if the browser warns. The mock user is `mock@cohad.local` with Resident + Administrator + Board roles owning 123 Mock Lane; `taylor@cohad.local` owns 456 Test Court. Data resets on restart.

## Architecture

### Backend (`Web/`)

- **Repository pattern:** All data access goes through interfaces in `Services/Repositories/`. Production implementations use Cosmos DB; `MockData` environment swaps in in-memory implementations. This is the key abstraction for testability and local development. A new Cosmos repository should mirror the existing ones (`CosmosEmailJobRepository`, `CosmosHeldMessageRepository`, `CosmosHomeRepository`):
  - **Id lookups use a point read** — `ReadItemAsync<JObject>(docId, PartitionKey.None)` with `catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound) return null` — not a `SELECT ... WHERE c.id` query.
  - **Populate `ETag` on every read path:** query results from `doc.Value<string>("_etag")` (set it in the `To<Model>` mapper so all query paths get it); point reads and writes (`Add`/`Upsert`) from `response.Headers.ETag` (capture the response — don't discard it).
  - **Keep the Mock impl behaviorally identical to the Cosmos impl** — same ETag handling, 409 on duplicate id, etc. Divergence between the two is a bug.
  - **Idempotency keys must be consistent:** the deterministic-id derivation, the dedup pre-check query, and any 409-conflict-recovery re-read must all key on the *same* fields, or duplicates slip through.
- **Auth:** JWT Bearer — Azure AD B2C in production (`cohadorgb2c.b2clogin.com`), HS256 mock tokens in `MockData` environment (via `GET /api/dev/mock-auth`, loopback only). Role-based authorization uses custom `RoleAuthorizationHandler` + policies (Resident, Administrator, WelcomeCommittee, GardenClub, Board, SocialCommittee, SunshineCommittee).
- **Role hierarchy:** Every Administrator is also assigned the Resident role. This is enforced at assignment time in `UserController.UpdateUserAssociations`. Controllers using `[Authorize(Policy = "Resident")]` therefore implicitly permit Administrators — do not add a redundant "Resident OR Administrator" check.
- **Startup:** `Startup.cs` wires auth, DI, and SPA proxy. `MockData` environment is selected solely by `ASPNETCORE_ENVIRONMENT=MockData` — there is no separate feature flag.
- **Open Graph:** `EventsController` serves `/events/{segment}` server-side (when `dist/cohad-app/index.html` exists) to inject OG meta tags for link previews.
- **Email unsubscribe:** Committee emails include per-recipient `List-Unsubscribe` / `List-Unsubscribe-Post` headers (RFC 8058) and an HTML footer with a link to the preference page. Tokens are AES-GCM encrypted using a 256-bit key derived from `UnsubscribeToken:SigningKey`, yielding an opaque base64url string (nonce + ciphertext + authentication tag). Callers must treat tokens as opaque. The public `UnsubscribeController` (`[AllowAnonymous]`) handles one-click unsubscribe and preference CRUD. Config: `UnsubscribeToken:SigningKey` (≥32 UTF-8 bytes) and `AppBaseUrl` (e.g. `https://www.cohad.org`). Without a signing key, emails are sent without unsubscribe headers/footer.
- **Notifications + escalation:** Unified in-app notifications (registrations, vendor flags, held committee emails) are raised via `INotificationService` and read audience-scoped through `NotificationsController`. `NotificationEscalationService` (a `BackgroundService` modeled on `CommitteeMailPoller`) sweeps on an interval and turns *unresolved* notifications that have aged past the grace period into **throttled email digests**, then stamps `EscalatedUtc`/`EscalationJobId` so each is emailed at most once. The sweep logic lives in the scoped `NotificationEscalationRunner` (unit-testable); recipients come from `INotificationRecipientResolver`, which resolves the audience to the same people who can act on it in-app — Administrators for `role:Administrator`, and committee *moderators* (`CommitteeAuthorization.CanManage`: admins + holders of the committee's `ManagementRole`) for `committee:{id}`, never plain committee members — then maps those users to emails; per-recipient throttle state is the `NotificationDigestState` Cosmos container. **There is no inline registration email anymore** — escalation is the email path, so `NotificationEscalation:Enabled` must be true (it is in `appsettings.json`; `appsettings.Development.json` disables it because Development has no Cosmos). Config knobs under `NotificationEscalation`: `Enabled`, `SweepIntervalMinutes` (15), `GracePeriodMinutes` (30), `MinDigestIntervalHours` (6), `MaxItemsPerDigest` (10). The service may run in `MockData` (in-memory repos + mock transport = no real send). Digest emails render each item as a **deep link** to its moderation page (`AppBaseUrl` + the notification's relative `DeepLink`); when `AppBaseUrl` is unset the digest degrades to plain titles. **Deployment requirement:** like every Cosmos container in this app, the `NotificationDigestState` container is provisioned out-of-band (not in code) — create it (non-partitioned, `/NoPartitionKey`) before enabling escalation in a new environment. If it is missing, the digest-state read fails fast and the sweep sends no escalation emails (the `GetAsync` 404 handler only swallows item-not-found, not a missing container).
- **Antispam hold for non-directory (held) emails:** `CommitteeMailPoller` holds committee emails from senders not in the directory, but does **not** notify moderators immediately. A held message enters an *antispam quarantine window* (`CommitteeForwarding:AntispamHoldMinutes`, default 60) — its `HeldMessage.NotifiedUtc` stays null — so automatic antispam has time to act before people are pulled in. Each poll cycle, `CommitteeMailPoller.NotifyHeldMessagesPastAntispamHoldAsync` sweeps held messages whose `HeldUtc` is past the window and still un-notified, raises the in-app notification (which then feeds escalation), and stamps `NotifiedUtc` (idempotent on the notification target, tolerant of a concurrent approve/reject; a held record missing its `CommitteeId` falls back to the Administrators audience). Query: `IHeldMessageRepository.GetAwaitingNotificationAsync(heldBeforeUtc)` (Cosmos + Mock, kept behaviorally identical). The sweep runs inside the poller, so it only fires while `CommitteeForwarding:Enabled` is true.
- **LLM spam classification of held emails:** Optionally, held (non-directory) messages are run through the Anthropic API (`ISpamClassifier` → `AnthropicSpamClassifier`, official `Anthropic` .NET SDK, Claude Haiku by default) to auto-filter obvious spam. Gated by `CommitteeForwarding:SpamClassification:Enabled` (a single kill-switch for both steps) plus an `Anthropic:ApiKey` (no key → a `DisabledSpamClassifier` no-op is registered and nothing is classified). The classifier runs **at hold time** in `HoldMessageAsync` (the body is in hand there) and stores its verdict/confidence/reason on the `HeldMessage` as a durable audit trail; the antispam sweep then **acts** on the stored verdict, so the quarantine window still elapses first (O365 keeps its chance). A message flagged `Spam` with confidence `>=` `SpamClassification:ConfidenceThreshold` (default `High`) is marked `Rejected` by `system:spam-classifier` **without** notifying moderators; everything else falls through to the normal notification path. Fail-safe throughout: any classifier error yields an `Unknown` verdict (never an exception), which never auto-rejects - so a classifier outage can only *under*-filter, never drop or wrongly reject mail. Auto-rejected records (and the original email, which stays in the mailbox's "COHAD Processed" folder) are retained, so a false positive is recoverable. Uses structured outputs (`Messages.Create<T>()` + `[SchemaProperty]`) so the model returns a validated `{isSpam, confidence, reason}` shape.

- **Scheduled maintenance jobs (user purge, PayPal sync):** Both run **in-process** as hosted services in `Web` (they were previously timer-triggered Azure Functions in a separate Function App; that project is gone). Each self-disables when its flag is off - `UserPurge:Enabled` / `PayPal:SyncEnabled`. The two pace themselves differently, on purpose:
  - **`UserPurgeService`** **checks** on startup and then every `UserPurge:IntervalHours` (24), but whether a check actually purges is decided by persisted state. Checking before waiting means a deploy cadence shorter than the interval can never silently skip the purge; consulting durable state means a host that recycles several times a day does not purge on every start, which would turn `MaxDeletesPerRun` from a per-interval cap on irreversible deletions into a per-restart cap. When not due it waits out only the remainder of the interval.
  - Pacing keys on **`LastAttemptUtc`, stamped *before* any deleting** - not on `LastSuccessUtc`. A sweep interrupted partway (host shutdown cancels `UserPurgeRunner` between candidates) has already performed some irreversible deletes, so pacing on success would let the next start run again with a fresh `MaxDeletesPerRun` budget and restore the very per-restart cap this state exists to prevent. `LastSuccessUtc` is stamped only when a sweep completes with `Errors == 0`, so a wholly failed purge is never recorded as healthy.
  - **`UserPurgeOptions.DryRun` defaults to `true`** on the type, not just in `appsettings.json`. A config source that supplies `UserPurge:Enabled` while omitting `DryRun` must not be able to perform irreversible deletions; the deleted Function host enforced the same fail-safe at its read site.
  - **`PayPalSyncService` + `PayPalSyncScheduler`** pace from a persisted timestamp, because the interval (`PayPal:SyncIntervalDays`, 7) is longer than the app's typical uptime between deploys. The service ticks every `PayPal:SyncCheckIntervalMinutes` (60) and the scoped scheduler (unit-testable, mirroring `NotificationEscalationRunner`) decides. `LastSuccessUtc` is stamped **only on success**, so a failure retries within hours instead of waiting out a week; `LastAttemptUtc` is stamped **before** the run and gates retries to `PayPal:SyncRetryIntervalHours` (6) so a bad credential can't produce an API call every tick. The sync's own date window is a rolling `[now - SyncLookbackDays, now)` computed inside the runner, so this state is purely pacing and never a correctness input.
  - **Deployment requirement:** the `BackgroundJobState` container (non-partitioned, `/NoPartitionKey`) must be provisioned out-of-band, like every other Cosmos container here. Both jobs use it. `CosmosBackgroundJobStateRepository.GetAsync` deliberately swallows **only** a 404 with sub-status 0; a missing container surfaces. Swallowing it would make every tick look like a first run, so the sync would hit the live PayPal API on every app start.
  - Requires **Always On** on the host - without it the app unloads when idle and neither timer fires.
  - **Every configured interval in the app is built through `JobInterval`**, which clamps to a range `Task.Delay` accepts - the two new services plus `NotificationEscalationService`, `CommitteeMailPoller`, and `EmailJobProcessor`. `Task.Delay` throws above ~49.7 days and an exception escaping a `BackgroundService` loop stops the whole host under the default `BackgroundServiceExceptionBehavior`, so an operator typing `SweepIntervalMinutes=525600` for "yearly" would otherwise take the site down rather than misconfigure one job. Add new interval config here, not to a raw `TimeSpan.From*`.
  - The jobs log their summaries at **Information**, so `appsettings.json` raises `Logging:LogLevel` for those four categories above the app-wide `Warning` default. Without that the cut-over runbook's `DryRun` verification step shows nothing at all.

### Frontend (`Web/ClientApp/`)

- **SPA served from backend:** In production, the backend serves the Angular dist. In development, it proxies to the ng serve port.
- **Auth:** `angular-oauth2-oidc` in production; `mock-auth.interceptor` + `mock-auth-token.service` inject dev tokens in mock mode (controlled by `environment.useMockAuth`).
- **Routing:** Lazy-loaded routes with `auth.guard` (requires login) and `role.guard` (requires specific roles). Public pages: home, about, news, events, email-preferences. Authenticated area: directory, map, vendors, youth-services, dues, myinfo, documents. Admin area: manage-users, manage-homes, manage-documents, manage-events, audit-log, send-email.
- **Environment configs:** `environment.ts` (dev), `environment.prod.ts`, `environment.mock.ts`.

### Cosmos DB config

Set via user secrets in the `Web` project directory:
```bash
dotnet user-secrets set "CosmosUri" "..."
dotnet user-secrets set "CosmosKey" "..."
dotnet user-secrets set "CosmosDatabase" "..."
```

The backend starts without these but all API calls fail at runtime.

### Telemetry (Application Insights)

Both the .NET backend and Angular frontend send telemetry to the **same** Application Insights resource. This enables correlated end-to-end traces (frontend page view → API request → Cosmos DB query). CORS correlation headers are enabled for API calls but excluded for `*.b2clogin.com` to avoid breaking auth flows.

**Backend:** Configured automatically via `services.AddApplicationInsightsTelemetry()` in `Startup.cs`. The connection string is in `appsettings.json` under `ApplicationInsights:ConnectionString`. To override per-environment, use `appsettings.{Environment}.json` or user secrets.

**Frontend:** `ApplicationInsightsService` (in `services/application-insights.service.ts`) initializes the `@microsoft/applicationinsights-web` v3.x SDK on app startup. The connection string is set per Angular build configuration in the environment files:
- `environment.prod.ts` — full connection string (telemetry enabled)
- `environment.ts` (dev) — empty string (telemetry **disabled**)
- `environment.mock.ts` — empty string (telemetry **disabled**)

When `appInsightsConnectionString` is empty, the service becomes a complete no-op: no SDK instance is created and no network requests are made.

**What is tracked on the frontend:**
- **Page views** — automatic on every Angular route navigation (`NavigationEnd` events)
- **Authenticated user context** — user's Azure AD B2C `sub` claim (opaque identifier, no PII), set on login and cleared on logout
- **Client-side exceptions** — all unhandled errors via `GlobalErrorHandler`
- **Custom events** — `DocumentDownloaded`, `VendorDetailViewed`, `VendorReviewSubmitted`, `EmailSent`, `EventSignupSubmitted`, `DirectorySearched`

**Changing the connection string:** If you need to point to a different Application Insights resource, update both:
1. `appsettings.json` → `ApplicationInsights:ConnectionString` (backend)
2. `environment.prod.ts` → `appInsightsConnectionString` (frontend)

## Gotchas

- `dotnet publish` runs `npm install` + `npm run prodbuild` automatically (via `PublishRunWebpack` target). Use `dotnet run` for development.
- Integration tests are skipped by default; set `RUN_COSMOS_INTEGRATION_TESTS=1` to enable.
- `Web.UnitTests` uses `InternalsVisibleTo` to access internal types — keep internal where appropriate.
- `ng lint` is not configured and will error. Use `ng build` for TypeScript type-checking.
- `MockJwt__SigningKey` must be ≥32 UTF-8 bytes for HS256. `appsettings.MockData.json` intentionally leaves it empty; supply via env var or `dotnet user-secrets`.
- `UnsubscribeToken__SigningKey` must be ≥32 UTF-8 bytes; it is used to derive an AES-GCM encryption key (via SHA-256) for unsubscribe tokens. Without it, emails are sent without unsubscribe headers/footer (graceful degradation). Supply via env var or `dotnet user-secrets set "UnsubscribeToken:SigningKey" "..."` in the `Web` project.
- `AppBaseUrl` must be set (e.g. `https://www.cohad.org`) for unsubscribe links in emails. Without it, no footer or headers are added.
- The `BackgroundJobState` Cosmos container must exist before enabling `UserPurge:Enabled` or `PayPal:SyncEnabled` (non-partitioned, `/NoPartitionKey`). Without it the PayPal sync fails fast on every tick rather than silently re-running.
- `Anthropic:ApiKey` (supply via env var or `dotnet user-secrets set "Anthropic:ApiKey" "..."` in the `Web` project) enables LLM spam classification of held committee emails. Without it, held-message classification is a no-op (a `DisabledSpamClassifier` is registered) even if `CommitteeForwarding:SpamClassification:Enabled` is true. The classifier is only wired alongside the mail poller, i.e. when `Graph` credentials are configured.

## Unit test policy

**Every new backend endpoint, service method, or non-trivial behavior change must include unit tests in the same PR.** Do not defer tests to a follow-up. If a code reviewer requests unit tests, write them immediately — do not reply with "will add later" or "acknowledged for follow-up."

Test expectations:
- New controller endpoints: test success path, authorization, input validation (400), and error/edge cases (409 conflict, 404 not found, etc.)
- New service logic: test core behavior, edge cases, and error handling
- Use the existing patterns in `Web.UnitTests/` (Moq mocks, `CreateController` helpers, xUnit `[Fact]`)
- Run `dotnet test Web.UnitTests/Web.UnitTests.csproj` to verify all tests pass before committing

## Backend code-review checklist

Beyond correctness, check these conventions before opening/refreshing a PR — they recur in review and are easy to miss:

- **Cosmos repositories:** see the repository-pattern conventions under Architecture → Backend (point reads, ETag on every path, Mock/Cosmos parity, consistent idempotency keys).
- **CancellationToken:** a method that accepts one must observe it — at minimum `ct.ThrowIfCancellationRequested()` up front, applied uniformly across the type's public methods (repositories take no token, so a service can't propagate further, but must still check).
- **Broad catch + cancellation:** `catch (Exception)` must exclude cancellation — `catch (Exception ex) when (ex is not OperationCanceledException)` — especially in `BackgroundService` paths, so shutdown isn't logged as a failure.
- **Parallelize independent I/O:** independent awaited calls should use `Task.WhenAll` (Cosmos SDK `Container` is thread-safe and the codebase already does this, e.g. cascade deletes) — fix it, don't just note it.
- **XML docs:** each member gets exactly one `<summary>` directly above it; inserting a method just above another can detach the next member's doc comment.
- **Contract enforcement:** an endpoint must enforce what its doc/comment promises (e.g. an "acknowledge only for type X" action must reject other types with 400, not silently act).
- **Test honesty:** a test's name must match its assertions (a `*_NewestFirst` test must actually assert ordering).
