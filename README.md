# COHAD

**Canyon Oaks Homeowners Association Directory** — a .NET 10 ASP.NET Core API plus an Angular 20 single-page app. The Web project hosts the API, serves the built SPA in production, and proxies to the Angular dev server in development.

This document covers how the app is structured, how to run it locally (including **MockData** mode without Cosmos or Azure AD B2C), which settings and environment variables matter, how deployment works, and how to tune **mock email job** behavior for UI testing.

For day-to-day AI/editor hints, see [`CLAUDE.md`](CLAUDE.md) and [`AGENTS.md`](AGENTS.md).

---

## Prerequisites

| Tool | Notes |
|------|--------|
| **.NET 10 SDK** | Required for `Web`, `Web.UnitTests`, and `Web.IntegrationTests`. |
| **Node.js 22+** and **npm** | For `Web/ClientApp` (`package-lock.json`). |

---

## Repository layout

| Path | Purpose |
|------|---------|
| [`COHAD.sln`](COHAD.sln) | Solution: Web, unit tests, integration tests. |
| [`Web/`](Web/) | ASP.NET Core app: APIs, `Startup.cs`, `ClientApp/` (Angular). |
| [`Web/ClientApp/`](Web/ClientApp/) | Angular SPA (`ng serve` in dev; `dist` produced on publish). |
| [`Web.UnitTests/`](Web.UnitTests/) | Fast unit tests (no Cosmos by default). |
| [`Web.IntegrationTests/`](Web.IntegrationTests/) | Cosmos-dependent tests (skipped unless `RUN_COSMOS_INTEGRATION_TESTS=1`). |
| [`scripts/run-mock-data.sh`](scripts/run-mock-data.sh) | MockData instructions; `./scripts/run-mock-data.sh api` generates signing keys and serves the API (same **https://127.0.0.1:5001** as Development). |

---

## How it fits together

- **Data access** uses a repository abstraction (`Web/Services/Repositories/`). **Production** uses Azure Cosmos DB; **`MockData`** environment swaps in **in-memory** repositories (`Web/MockData/`). There is no separate “use mock” flag — only `ASPNETCORE_ENVIRONMENT=MockData`.

- **Authentication**: **Production/Development** use JWTs from **Azure AD B2C** (`cohadorgb2c.b2clogin.com`). **MockData** validates **HS256** tokens issued by `GET /api/dev/mock-auth` (only when environment is MockData and the request is from loopback).

- **SPA**: In **Development** and **MockData**, the backend **proxies** browser requests to the Angular dev server (`http://127.0.0.1:4200` by default). In **production**, static files are served from `ClientApp/dist/cohad-app` after `npm run prodbuild`.

- **Email**: Committee sends queue **email jobs** processed by a background service (`EmailJobProcessor`). Non-test sends use **SMTP** (MailKit) when not in MockData. **MockData** simulates sends in-process (no SMTP) and can be tuned for UI testing (see [Mock email jobs](#mock-email-jobs-mockdata-only)).

---

## Running locally — Development (Cosmos + B2C)

**Development** and **MockData** both serve the API over **HTTPS** at **`https://127.0.0.1:5001`** (ASP.NET Core **development certificate**). Trust it once on your machine:

```bash
dotnet dev-certs https --trust
```

Typical flow uses **two terminals**: Angular on port **4200**, API on **5001** (HTTPS).

**Terminal 1 — Angular**

```bash
cd Web/ClientApp
npx ng serve --host 127.0.0.1 --port 4200
```

Use **`127.0.0.1`** so the backend proxy matches (avoid IPv4/IPv6 mismatches).

**Terminal 2 — API**

```bash
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="https://127.0.0.1:5001" dotnet run --project Web/Web.csproj
```

Open **https://127.0.0.1:5001**. Sign-in uses **real B2C**; the SPA uses `window.location.origin` as the OAuth redirect URI, so register **`https://127.0.0.1:5001`** (or the path your flow requires) in the Azure AD B2C app. You also need working **Cosmos** settings or most data APIs will fail.

**Cosmos DB** (required for real data) — from the `Web` directory:

```bash
dotnet user-secrets set "CosmosUri" "https://<account>.documents.azure.com:443/"
dotnet user-secrets set "CosmosKey" "<key>"
dotnet user-secrets set "CosmosDatabase" "<database-name>"
```

The app starts without these, but **data APIs fail at runtime** until they are set.

---

## Running locally — MockData (no Cosmos, no B2C)

Use this for **local UX testing** with a fixed in-memory directory and **mock JWT** auth.

**Terminal 1 — Angular with mock auth** (uses `environment.mock.ts` / `useMockAuth`)

```bash
cd Web/ClientApp
npm run start:mock
```

**Terminal 2 — API**

From the **repository root**, either:

- **One command** (generates fresh **OpenSSL** secrets and starts the API):

  ```bash
  ./scripts/run-mock-data.sh api
  ```

- **Or** copy the one-liner printed by `./scripts/run-mock-data.sh` (same behavior: `openssl rand -hex 32` for each key, then `dotnet run`).

Signing keys must be at least **32 UTF-8 bytes**; the script uses **64 hex characters** per key, which satisfies that.

MockData uses the **same HTTPS URL and development certificate** as [Development](#running-locally--development-cosmos--b2c) above. Open **https://127.0.0.1:5001**. The SPA obtains a short-lived token from **`GET /api/dev/mock-auth`** (loopback only).

**Seeded mock users** (reset on every restart):

| User | Notes |
|------|--------|
| **mock@cohad.local** | Resident, **Administrator**, **Board**; owns **123 Mock Lane**. Use for manage UI and **Send Email** as Board. |
| **taylor@cohad.local** | Resident only; owns **456 Test Court** — useful for admin management flows. |

Seeded homes include opted-in **board** email addresses so **Send neighborhood email** as Board has multiple recipients.

---

## Configuration reference

ASP.NET Core merges `appsettings.json`, `appsettings.{Environment}.json`, environment variables, and user secrets. Nested keys map to env vars with **`__`** (double underscore), e.g. `EmailJobs__Mock__DelayMilliseconds`.

### Core application

| Setting | Description |
|---------|-------------|
| `ApplicationInsights:ConnectionString` | Backend telemetry; in [`Web/appsettings.json`](Web/appsettings.json). Override per environment or user secrets if needed. |
| `AllowedOrigins` | CORS origins (see [`Web/appsettings.Development.json`](Web/appsettings.Development.json)). |
| `AppBaseUrl` | Public site base URL (e.g. `https://www.cohad.org`). Used for **unsubscribe links** in real emails. If unset, unsubscribe headers/footer are omitted. |

### Cosmos DB (production / Development)

| Setting | Env var example |
|---------|-----------------|
| `CosmosUri` | `CosmosUri` |
| `CosmosKey` | `CosmosKey` |
| `CosmosDatabase` | `CosmosDatabase` |

### Document / blob storage

| Setting | Description |
|---------|-------------|
| `DocumentStorage:ConnectionString` | Azure Storage connection for document uploads. |
| `DocumentStorage:ContainerName` | Blob container (default in appsettings). |
| `DocumentStorage:MaxUploadBytes` | Upload size limit (multipart limit is aligned in `Startup.cs`). |

### SMTP (real email — not MockData)

Used by synchronous email paths and by the **SMTP** branch of `EmailJobProcessor` (non-mock environments):

| Setting | Description |
|---------|-------------|
| `SmtpHost` | SMTP host (port **587** + STARTTLS in code). |
| `SmtpUser` | SMTP username. |
| `SmtpPassword` | SMTP password. |

### Email jobs (all environments)

| Setting | Default | Description |
|---------|---------|-------------|
| `EmailJobs:Enabled` | `true` | Set `false` to disable the background processor entirely. |
| `EmailJobs:DefaultMaxRecipientAttempts` | `3` | Per-recipient retry cap. |
| `EmailJobs:StallAfterMinutes` | `30` | Incomplete jobs with no progress can be marked stalled on restart. |
| `EmailJobs:LogSmtpProtocolOnFailure` | `false` | Extra SMTP logging on failure. |
| `EmailJobs:RetentionDays` | `90` | Deletes terminal email jobs older than this age (cleanup runs when a new job is submitted). |
| `EmailJobs:CleanupBatchSize` | `25` | Max number of old jobs deleted per submission-triggered cleanup pass. |

### Mock email jobs (MockData only)

When `ASPNETCORE_ENVIRONMENT=MockData`, bulk sends are **simulated** (no SMTP). Tune behavior via **`EmailJobs:Mock`** ([`Web/appsettings.MockData.json`](Web/appsettings.MockData.json) or environment variables):

| Setting | Example env var | Description |
|---------|-------------------|-------------|
| `EmailJobs:Mock:DelayMilliseconds` | `EmailJobs__Mock__DelayMilliseconds` | Pause per **recipient** after a simulated attempt (ms). Makes progress bars visible; default in MockData config is **300**; code fallback is **250** if unset in MockData. |
| `EmailJobs:Mock:RandomFailureProbability` | `EmailJobs__Mock__RandomFailureProbability` | **0.0–1.0** — independent random failure per recipient (partial / failed jobs, **Retry** UI). |
| `EmailJobs:Mock:RandomFailSeed` | `EmailJobs__Mock__RandomFailSeed` | Optional **int** — fixes the RNG for repeatable “random” failures. |
| `EmailJobs:Mock:FailAllRecipients` | `EmailJobs__Mock__FailAllRecipients` | `true` — every simulated send fails (exercise **Failed** / **Retry**). |
| `EmailJobs:Mock:JobFatalError` | `EmailJobs__Mock__JobFatalError` | Non-empty string — job fails **immediately** with this `LastError` (no per-recipient simulation). |

**Test email** from the Send Email page goes through the job queue in all environments (MockData uses simulated delays, no SMTP).

**Cancel / retry**: Use **Manage → Email Jobs** and the job detail page; behavior is the same as production (SignalR progress + REST APIs).

### Mock JWT and unsubscribe (MockData)

| Setting | Notes |
|---------|--------|
| `MockJwt:SigningKey` | **≥32 UTF-8 bytes.** Required for mock HS256 tokens. Often set via **`MockJwt__SigningKey`**. [`appsettings.MockData.json`](Web/appsettings.MockData.json) leaves this empty on purpose — set in env or user secrets. |
| `UnsubscribeToken:SigningKey` | **≥32 UTF-8 bytes** for AES-GCM unsubscribe tokens. **`UnsubscribeToken__SigningKey`** in env. If missing, mock runs but committee email footers/tokens degrade as in production. |

### Azure AD B2C (non-MockData)

Issuer, audience, and OIDC metadata are **hard-coded in [`Web/Startup.cs`](Web/Startup.cs)** for production-style JWT validation. Changing tenants requires code changes there.

---

## Build, test, and publish

**Backend**

```bash
dotnet build Web/Web.csproj
dotnet test Web.UnitTests/Web.UnitTests.csproj
RUN_COSMOS_INTEGRATION_TESTS=1 dotnet test Web.IntegrationTests/Web.IntegrationTests.csproj
```

**Frontend** (from `Web/ClientApp`)

```bash
npx ng build          # type-check / production build
npx ng test --no-watch --browsers=ChromeHeadless
```

There is **no** working `ng lint` target in this repo; use `ng build` for TypeScript checking.

**Publish**

`dotnet publish` on **`Web/Web.csproj`** runs **`npm install`** and **`npm run prodbuild`** (see `PublishRunWebpack` in [`Web/Web.csproj`](Web/Web.csproj)). Prefer **`dotnet run`** for local development to avoid slow publishes.

---

## Deployment (high level)

1. **Build/publish** the `Web` project so the Angular `dist` output is included and the API is compiled.
2. **Host** the published output on your platform (e.g. Azure App Service, container, IIS + Kestrel).
3. **Configure** production settings via environment variables or Azure App Settings: **Cosmos**, **document storage**, **SMTP**, **`AppBaseUrl`**, **`UnsubscribeToken:SigningKey`**, Application Insights, etc.
4. **Do not** run with `ASPNETCORE_ENVIRONMENT=MockData` in production — MockData is for local/testing only.
5. **Scheduled jobs** (user purge, PayPal sync) run in-process as hosted services in the `Web` app - there is nothing separate to deploy. Both are off by default; enable via `UserPurge__Enabled` / `PayPal__SyncEnabled`. Both require a **`BackgroundJobState`** Cosmos container (non-partitioned, `/NoPartitionKey`), provisioned out-of-band like every other container, which is what paces them across restarts. **Always On** must be enabled on the host, or the app unloads when idle and the timers never fire.

### Decommissioning the old UserPurge Function App

These jobs previously ran as timer-triggered Azure Functions. Deleting the project and its workflow from this repo does **not** stop the deployed Function App - it still exists in Azure with its own App Settings and its last-deployed code, and it points at the same Cosmos database. If both run, PayPal transactions are imported twice (payments get a random `id`, so the dedup read-then-write does not catch a concurrent importer) and the purge double-writes audit entries.

Cut over in this order:

1. Deploy the `Web` app with `UserPurge__Enabled=false` and `PayPal__SyncEnabled=false`.
2. On the **Function App**, set `UserPurge__Enabled=false` and `PayPal__SyncEnabled=false`, or stop the app outright.
3. Enable the flags on the **Web** app and confirm from the logs that a run happens.
4. Delete the Function App, and the storage account it used for `AzureWebJobsStorage` if nothing else does. Check whether it shares the web app's Application Insights resource before deleting that.
5. Remove the now-unused GitHub secrets `AzureFunctionApp_Name_UserPurge` and `AzureFunctionApp_PublishProfile_UserPurge`.

---

## Troubleshooting

| Issue | What to check |
|-------|----------------|
| SPA proxy connection refused | Angular must listen on **`127.0.0.1:4200`** (use `npm start` / `ng serve --host 127.0.0.1`). |
| MockData: 500 on mock-auth | **`MockJwt__SigningKey`** length ≥ 32 bytes. |
| Browser warns on local HTTPS | Run **`dotnet dev-certs https --trust`**. Open **https://127.0.0.1:5001** (Development and MockData). |
| APIs fail with Cosmos | Set **CosmosUri / CosmosKey / CosmosDatabase** (user secrets or env). |
| Committee emails missing unsubscribe | **`UnsubscribeToken:SigningKey`** and **`AppBaseUrl`** in production. |

---

## License / contributing

(Add your license and contribution guidelines here if applicable.)
