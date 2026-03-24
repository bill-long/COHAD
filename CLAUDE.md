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
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="http://0.0.0.0:5000" dotnet run --project Web/Web.csproj
```

The backend proxies SPA requests to `http://127.0.0.1:4200`. Use `--host 127.0.0.1` (not the default) to avoid IPv4/IPv6 proxy mismatches.

### Mock data mode (no Cosmos DB or Azure AD B2C required)

```bash
# Terminal 1
cd Web/ClientApp && npm run start:mock

# Terminal 2
MockJwt__SigningKey='<32+ UTF-8 byte secret>' ASPNETCORE_ENVIRONMENT=MockData ASPNETCORE_URLS="http://127.0.0.1:5000" dotnet run --project Web/Web.csproj
```

Or use `scripts/run-mock-data.sh`. Open http://127.0.0.1:5000. The mock user is `mock@cohad.local` with Resident + Administrator roles owning 123 Mock Lane; `taylor@cohad.local` owns 456 Test Court. Data resets on restart.

## Architecture

### Backend (`Web/`)

- **Repository pattern:** All data access goes through interfaces in `Services/Repositories/`. Production implementations use Cosmos DB; `MockData` environment swaps in in-memory implementations. This is the key abstraction for testability and local development.
- **Auth:** JWT Bearer — Azure AD B2C in production (`cohadorgb2c.b2clogin.com`), HS256 mock tokens in `MockData` environment (via `GET /api/dev/mock-auth`, loopback only). Role-based authorization uses custom `RoleAuthorizationHandler` + policies (Resident, Administrator, WelcomeCommittee, GardenClub, Board, SocialCommittee, SunshineCommittee).
- **Role hierarchy:** Every Administrator is also assigned the Resident role. This is enforced at assignment time in `UserController.UpdateUserAssociations`. Controllers using `[Authorize(Policy = "Resident")]` therefore implicitly permit Administrators — do not add a redundant "Resident OR Administrator" check.
- **Startup:** `Startup.cs` wires auth, DI, and SPA proxy. `MockData` environment is selected solely by `ASPNETCORE_ENVIRONMENT=MockData` — there is no separate feature flag.
- **Open Graph:** `EventsController` serves `/events/{segment}` server-side (when `dist/cohad-app/index.html` exists) to inject OG meta tags for link previews.

### Frontend (`Web/ClientApp/`)

- **SPA served from backend:** In production, the backend serves the Angular dist. In development, it proxies to the ng serve port.
- **Auth:** `angular-oauth2-oidc` in production; `mock-auth.interceptor` + `mock-auth-token.service` inject dev tokens in mock mode (controlled by `environment.useMockAuth`).
- **Routing:** Lazy-loaded routes with `auth.guard` (requires login) and `role.guard` (requires specific roles). Public pages: home, about, news, events. Authenticated area: directory, map, vendors, youth-services, dues, myinfo, documents. Admin area: manage-users, manage-homes, manage-documents, manage-events, audit-log, send-email.
- **Environment configs:** `environment.ts` (dev), `environment.prod.ts`, `environment.mock.ts`.

### Cosmos DB config

Set via user secrets in the `Web` project directory:
```bash
dotnet user-secrets set "CosmosUri" "..."
dotnet user-secrets set "CosmosKey" "..."
dotnet user-secrets set "CosmosDatabase" "..."
```

The backend starts without these but all API calls fail at runtime.

## Gotchas

- `dotnet publish` runs `npm install` + `npm run prodbuild` automatically (via `PublishRunWebpack` target). Use `dotnet run` for development.
- Integration tests are skipped by default; set `RUN_COSMOS_INTEGRATION_TESTS=1` to enable.
- `Web.UnitTests` uses `InternalsVisibleTo` to access internal types — keep internal where appropriate.
- `ng lint` is not configured and will error. Use `ng build` for TypeScript type-checking.
- `MockJwt__SigningKey` must be ≥32 UTF-8 bytes for HS256. `appsettings.MockData.json` intentionally leaves it empty; supply via env var or `dotnet user-secrets`.
