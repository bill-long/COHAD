# Copilot Instructions

## Project overview

COHAD (Canyon Oaks Homeowners Association Directory) is a .NET 10 ASP.NET Core backend + Angular 20 SPA frontend. See `COHAD.sln` for the full solution structure (Web, Web.UnitTests, Web.IntegrationTests, Functions/UserPurgeFunction).

## Prerequisites

- .NET 10 SDK
- Node.js 22+
- npm (lockfile: `Web/ClientApp/package-lock.json`)

## Running the app (dev mode)

1. Start Angular dev server: `cd Web/ClientApp && npx ng serve --host 0.0.0.0 --port 4200`
2. Start .NET backend: `ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="http://0.0.0.0:5000" dotnet run --project Web/Web.csproj`
3. The backend proxies SPA requests to `http://127.0.0.1:4200` in development mode.

## Mock data mode (agents / local UX testing)

Use this when you need a **signed-in** session and working APIs **without** Cosmos DB or Azure AD B2C.

1. **Angular** with mock auth: `cd Web/ClientApp && npm run start:mock` (build configuration `mock` sets `environment.useMockAuth`).
2. **Backend** with in-memory repositories and HS256 dev tokens — supply a **signing key** of at least **32 UTF-8 bytes** via environment variable **`MockJwt__SigningKey`**:
   ```
   MockJwt__SigningKey='<your-local-secret>' ASPNETCORE_ENVIRONMENT=MockData ASPNETCORE_URLS="http://127.0.0.1:5000" dotnet run --project Web/Web.csproj
   ```
3. Open **http://127.0.0.1:5000** (same proxy pattern as Development).

The SPA obtains a dev JWT from `GET /api/dev/mock-auth` (only available in `MockData` environment from loopback requests). The token lifetime is 15 minutes. The mock user is **mock@cohad.local** with **Resident** and **Administrator** roles, owning a sample home at **123 Mock Lane**. Data resets when the process restarts.

Mock mode is selected only by **`ASPNETCORE_ENVIRONMENT=MockData`**. Use `scripts/run-mock-data.sh` to run both commands together.

## Tests

- **Backend unit tests** (no external deps): `dotnet test Web.UnitTests/Web.UnitTests.csproj`
- **Frontend tests** (Karma/Jasmine, headless Chrome): `cd Web/ClientApp && npx ng test --no-watch --browsers=ChromeHeadless`
- **Integration tests** (require Cosmos DB emulator or account): `RUN_COSMOS_INTEGRATION_TESTS=1 dotnet test Web.IntegrationTests/Web.IntegrationTests.csproj` — skipped by default when env var is absent.

## Lint / type-check

No Angular lint target is configured (`ng lint` errors). The project has a legacy `tslint.json` without an architect target. Use `npx ng build` as the primary TypeScript type check. The TypeScript config uses strict mode and Angular `strictTemplates`.

## Non-obvious gotchas

- The backend starts successfully without Cosmos DB credentials but API endpoints fail at runtime (null CosmosClient). The SPA landing page, Privacy Policy, and navigation work without Cosmos.
- Authentication uses Azure AD B2C (`cohadorgb2c.b2clogin.com`). Sign In redirects externally and requires a registered redirect URI matching the dev environment.
- The `.csproj` `PublishRunWebpack` target runs `npm install` + `npm run prodbuild` during `dotnet publish` — avoid publishing in dev; use `dotnet run` instead.
- Cosmos DB config is via user secrets (`CosmosUri`, `CosmosKey`, `CosmosDatabase`). Set them with `dotnet user-secrets set` in the `Web` project directory.
- Never commit secrets or signing keys. `appsettings.MockData.json` keeps an empty placeholder for `MockJwt:SigningKey`.
