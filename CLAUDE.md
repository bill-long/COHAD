# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project overview

COHAD (Canyon Oaks Homeowners Association Directory) is a .NET 10 ASP.NET Core backend + Angular 20 SPA frontend. See `COHAD.sln` for the full solution structure (Web, Web.UnitTests, Web.IntegrationTests, Functions/UserPurgeFunction).

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

- **Repository pattern:** All data access goes through interfaces in `Services/Repositories/`. Production implementations use Cosmos DB; `MockData` environment swaps in in-memory implementations. This is the key abstraction for testability and local development.
- **Auth:** JWT Bearer — Azure AD B2C in production (`cohadorgb2c.b2clogin.com`), HS256 mock tokens in `MockData` environment (via `GET /api/dev/mock-auth`, loopback only). Role-based authorization uses custom `RoleAuthorizationHandler` + policies (Resident, Administrator, WelcomeCommittee, GardenClub, Board, SocialCommittee, SunshineCommittee).
- **Role hierarchy:** Every Administrator is also assigned the Resident role. This is enforced at assignment time in `UserController.UpdateUserAssociations`. Controllers using `[Authorize(Policy = "Resident")]` therefore implicitly permit Administrators — do not add a redundant "Resident OR Administrator" check.
- **Startup:** `Startup.cs` wires auth, DI, and SPA proxy. `MockData` environment is selected solely by `ASPNETCORE_ENVIRONMENT=MockData` — there is no separate feature flag.
- **Open Graph:** `EventsController` serves `/events/{segment}` server-side (when `dist/cohad-app/index.html` exists) to inject OG meta tags for link previews.
- **Email unsubscribe:** Committee emails include per-recipient `List-Unsubscribe` / `List-Unsubscribe-Post` headers (RFC 8058) and an HTML footer with a link to the preference page. Tokens are AES-GCM encrypted using a 256-bit key derived from `UnsubscribeToken:SigningKey`, yielding an opaque base64url string (nonce + ciphertext + authentication tag). Callers must treat tokens as opaque. The public `UnsubscribeController` (`[AllowAnonymous]`) handles one-click unsubscribe and preference CRUD. Config: `UnsubscribeToken:SigningKey` (≥32 UTF-8 bytes) and `AppBaseUrl` (e.g. `https://www.cohad.org`). Without a signing key, emails are sent without unsubscribe headers/footer.

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

## Unit test policy

**Every new backend endpoint, service method, or non-trivial behavior change must include unit tests in the same PR.** Do not defer tests to a follow-up. If a code reviewer requests unit tests, write them immediately — do not reply with "will add later" or "acknowledged for follow-up."

Test expectations:
- New controller endpoints: test success path, authorization, input validation (400), and error/edge cases (409 conflict, 404 not found, etc.)
- New service logic: test core behavior, edge cases, and error handling
- Use the existing patterns in `Web.UnitTests/` (Moq mocks, `CreateController` helpers, xUnit `[Fact]`)
- Run `dotnet test Web.UnitTests/Web.UnitTests.csproj` to verify all tests pass before committing
