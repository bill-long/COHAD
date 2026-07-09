# AGENTS.md

## Cursor Cloud specific instructions

### Project overview

COHAD (Canyon Oaks Homeowners Association Directory) is a .NET 10 ASP.NET Core backend + Angular 20 SPA frontend. See `COHAD.sln` for the full solution structure (Web, Web.UnitTests, Web.IntegrationTests, Functions/UserPurgeFunction).

### Prerequisites

- .NET 10 SDK (`dotnet-sdk-10.0` from Ubuntu repos)
- Node.js 22+ (pre-installed via nvm)
- npm (lockfile: `Web/ClientApp/package-lock.json`)

### Running the app (dev mode)

1. Start Angular dev server: `cd Web/ClientApp && npx ng serve --host 0.0.0.0 --port 4200`
2. Start .NET backend: `ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="https://127.0.0.1:5001" dotnet run --project Web/Web.csproj` (trust the dev cert once: `dotnet dev-certs https --trust`)
3. The backend proxies SPA requests to `http://127.0.0.1:4200` in development mode. Open **https://127.0.0.1:5001**.

### Mock data mode (agents / local UX testing)

Use this when you need a **signed-in** session and working APIs **without** Cosmos DB or Azure AD B2C.

1. **Angular** with mock auth: `cd Web/ClientApp && npm run start:mock` (build configuration `mock` sets `environment.useMockAuth`).
2. **Backend** with in-memory repositories and HS256 dev tokens — from repo root run **`./scripts/run-mock-data.sh api`** (generates `MockJwt` / `UnsubscribeToken` secrets with OpenSSL and starts the API). Or run `./scripts/run-mock-data.sh` with no args and paste the printed one-liner. You can still set keys manually or via `dotnet user-secrets set "MockJwt:SigningKey" "..."` if you prefer (`appsettings.MockData.json` keeps empty placeholders; never commit real keys).
3. Open **https://127.0.0.1:5001** (same HTTPS URL as Development; run `dotnet dev-certs https --trust` once if needed). Same proxy pattern: API serves the app and proxies the SPA from port 4200.

The SPA obtains a dev JWT from `GET /api/dev/mock-auth` (only when `ASPNETCORE_ENVIRONMENT=MockData` and only from loopback requests). The token lifetime is short-lived (15 minutes). The signed-in mock user is **mock@cohad.local** with **Resident**, **Administrator**, and **Board** roles and owns **123 Mock Lane**. Mock data also seeds a second user, **taylor@cohad.local**, who owns **456 Test Court** so administrators can exercise user/home/role management flows. Data resets when the process restarts.

Mock mode is selected only by **`ASPNETCORE_ENVIRONMENT=MockData`** (not a separate `UseMockData` flag). This mode is for local testing only.

### Tests

- **Backend unit tests** (no external deps): `dotnet test Web.UnitTests/Web.UnitTests.csproj`
- **Frontend tests** (Karma/Jasmine, headless Chrome): `cd Web/ClientApp && npx ng test --no-watch --browsers=ChromeHeadless`
- **Integration tests** (require Cosmos DB emulator or account): `RUN_COSMOS_INTEGRATION_TESTS=1 dotnet test Web.IntegrationTests/Web.IntegrationTests.csproj` — skipped by default when env var is absent.

### Lint

No Angular lint target is configured (`ng lint` errors). The project has a legacy `tslint.json` without an architect target. TypeScript compilation (`ng build`) serves as the primary type check.

### Facebook / Open Graph (event deep links)

When `ClientApp/dist/cohad-app/index.html` exists (published or local `ng build`), `GET /events/{segment}` is handled on the server so the initial HTML includes Open Graph meta tags for link previews. `{segment}` is the public slug (e.g. `2025-neighborhood-picnic`) or a legacy GUID string. In Development with no dist build, `/events/…` is served by the SPA dev proxy instead (no server-injected OG tags until a build exists). After changing titles, descriptions, or promo images, use [Facebook Sharing Debugger](https://developers.facebook.com/tools/debug/) with your deployed URL, then choose **Scrape Again** so Facebook refreshes its cached preview.

### Non-obvious gotchas

- **SPA dev proxy vs `ng serve` host:** `Startup.cs` proxies to `http://127.0.0.1:4200`. Plain `ng serve` (no `--host`) may listen only on `localhost` / IPv6 (`::1`), so the proxy gets “connection refused” to `127.0.0.1`. Use `npm start` (binds `127.0.0.1`), or `npx ng serve --host 127.0.0.1 --port 4200`, or `--host 0.0.0.0` which also accepts IPv4 loopback.
- The backend starts successfully without Cosmos DB credentials but API endpoints will fail at runtime (null CosmosClient). The SPA landing page, Privacy Policy, and navigation all work without Cosmos.
- Authentication uses Azure AD B2C (`cohadorgb2c.b2clogin.com`). Sign In will redirect externally; this cannot work without a registered redirect URI matching the dev environment.
- The `.csproj` `PublishRunWebpack` target runs `npm install` + `npm run prodbuild` during `dotnet publish` — avoid publishing in dev; use `dotnet run` instead.
- Cosmos DB config is via user secrets (`CosmosUri`, `CosmosKey`, `CosmosDatabase`). Set them with `dotnet user-secrets set` in the `Web` project directory.
- **Email unsubscribe config:** `UnsubscribeToken:SigningKey` (≥32 UTF-8 bytes) and `AppBaseUrl` (e.g. `https://www.cohad.org`) enable per-recipient unsubscribe links in committee emails. Without the signing key, emails are sent without unsubscribe headers/footer (graceful degradation). Set via env var (`UnsubscribeToken__SigningKey`) or user secrets. The public `/email-preferences` Angular route and `UnsubscribeController` API endpoints require no authentication.

### Telemetry (Application Insights)

Both the .NET backend and Angular frontend send telemetry to the **same** Application Insights resource, enabling correlated end-to-end traces (frontend page view → API request → Cosmos DB query). CORS correlation headers are enabled for API calls but excluded for `*.b2clogin.com` to avoid breaking auth flows.

**Backend:** Configured via `services.AddApplicationInsightsTelemetry()` in `Startup.cs`. Connection string is in `appsettings.json` under `ApplicationInsights:ConnectionString`.

**Frontend:** `ApplicationInsightsService` (`services/application-insights.service.ts`) initializes the `@microsoft/applicationinsights-web` v3.x SDK. The connection string is set per Angular build configuration in the environment files:
- `environment.prod.ts` — full connection string (telemetry enabled)
- `environment.ts` (dev) and `environment.mock.ts` — empty string (telemetry **disabled**, complete no-op)

**What is tracked on the frontend:**
- **Page views** — automatic on every route navigation
- **Authenticated user context** — Azure AD B2C `sub` claim (opaque identifier, no PII)
- **Client-side exceptions** — all unhandled errors via `GlobalErrorHandler`
- **Custom events** — `DocumentDownloaded`, `VendorDetailViewed`, `VendorReviewSubmitted`, `EmailSent`, `EventSignupSubmitted`, `DirectorySearched`

**To change the target resource**, update both `appsettings.json` → `ApplicationInsights:ConnectionString` (backend) and `environment.prod.ts` → `appInsightsConnectionString` (frontend).

### Unit test policy

**Every new backend endpoint, service method, or non-trivial behavior change must include unit tests in the same PR.** Do not defer tests to a follow-up. If a code reviewer requests unit tests, write them immediately — do not reply with "will add later" or "acknowledged for follow-up."

Test expectations:
- New controller endpoints: test success path, authorization, input validation (400), and error/edge cases (409 conflict, 404 not found, etc.)
- New service logic: test core behavior, edge cases, and error handling
- Use the existing patterns in `Web.UnitTests/` (Moq mocks, `CreateController` helpers, xUnit `[Fact]`)
- Run `dotnet test Web.UnitTests/Web.UnitTests.csproj` to verify all tests pass before committing

### Review gate

Non-trivial changes ship through a pull request, never a direct push to `master`. `master` is branch-protected: a PR is required and every review conversation must be resolved before merge. Protection is enforced for admins too, so an agent operating with admin credentials cannot bypass it.

The review loop for a PR is mandatory. Run a local review (`/code-review` at high, or the review workflow), then request a Copilot review. The terminal condition is **"every review thread is resolved and required checks are green"** - it is NOT "the latest Copilot pass returned no new comments." A clean incremental Copilot pass does not mean the earlier, more thorough findings were all resolved; treat each finding as open until it is fixed or explicitly tracked.

Every review finding - from a local review, the review workflow, or Copilot - must reach a terminal state before the task is considered done:
- **Fixed** in the PR, or
- **Deferred or accepted:** open a GitHub issue labeled `deferred-finding` that states the finding and the reason it is not being fixed now, link that issue in the review thread, then resolve the thread.

Never silently drop a finding, and never resolve a review thread without either a fix or a linked tracking issue. When you **narrow-fix** - address one instance or one code path of a finding but not the broader case it describes - file a `deferred-finding` issue for the remainder rather than resolving the thread as if fully fixed. (This is the exact gap that let real issues merge before: a finding was fixed at its narrowest instance and the rest fell off the list.)
