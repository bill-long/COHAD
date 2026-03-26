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
2. Start .NET backend: `ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="http://0.0.0.0:5000" dotnet run --project Web/Web.csproj`
3. The backend proxies SPA requests to `http://127.0.0.1:4200` in development mode.

### Mock data mode (agents / local UX testing)

Use this when you need a **signed-in** session and working APIs **without** Cosmos DB or Azure AD B2C.

1. **Angular** with mock auth: `cd Web/ClientApp && npm run start:mock` (build configuration `mock` sets `environment.useMockAuth`).
2. **Backend** with in-memory repositories and HS256 dev tokens — you must supply a **signing key** of at least **32 UTF-8 bytes** (required for HS256) via environment variable **`MockJwt__SigningKey`** or `dotnet user-secrets set "MockJwt:SigningKey" "..."` in the `Web` project (never commit real keys; `appsettings.MockData.json` keeps an empty placeholder):
    `MockJwt__SigningKey='<your-local-secret>' ASPNETCORE_ENVIRONMENT=MockData ASPNETCORE_URLS="http://127.0.0.1:5000" dotnet run --project Web/Web.csproj`
3. Open **http://127.0.0.1:5000** (same proxy pattern as Development: API serves the app and proxies the SPA from port 4200).

The SPA obtains a dev JWT from `GET /api/dev/mock-auth` (only when `ASPNETCORE_ENVIRONMENT=MockData` and only from loopback requests). The token lifetime is short-lived (15 minutes). The signed-in mock user is **mock@cohad.local** with **Resident** and **Administrator** roles and owns **123 Mock Lane**. Mock data also seeds a second user, **taylor@cohad.local**, who owns **456 Test Court** so administrators can exercise user/home/role management flows. Data resets when the process restarts.

Mock mode is selected only by **`ASPNETCORE_ENVIRONMENT=MockData`** (not a separate `UseMockData` flag). This mode is for local testing only.

See also `scripts/run-mock-data.sh` for the two commands in one place.

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

### Telemetry (Application Insights)

Both the .NET backend and Angular frontend send telemetry to the **same** Application Insights resource, enabling correlated end-to-end traces (frontend page view → API request → Cosmos DB query).

**Backend:** Configured via `services.AddApplicationInsightsTelemetry()` in `Startup.cs`. Connection string is in `appsettings.json` under `ApplicationInsights:ConnectionString`.

**Frontend:** `ApplicationInsightsService` (`services/application-insights.service.ts`) initializes the `@microsoft/applicationinsights-web` v3.x SDK. The connection string is set per Angular build configuration in the environment files:
- `environment.prod.ts` — full connection string (telemetry enabled)
- `environment.ts` (dev) and `environment.mock.ts` — empty string (telemetry **disabled**, complete no-op)

**What is tracked on the frontend:**
- **Page views** — automatic on every route navigation
- **Authenticated user context** — Azure AD B2C `sub` claim + email
- **Client-side exceptions** — all unhandled errors via `GlobalErrorHandler`
- **Custom events** — `DocumentDownloaded`, `VendorDetailViewed`, `VendorReviewSubmitted`, `EmailSent`, `EventSignupSubmitted`, `DirectorySearched`

**To change the target resource**, update both `appsettings.json` → `ApplicationInsights:ConnectionString` (backend) and `environment.prod.ts` → `appInsightsConnectionString` (frontend).
