# UserPurgeFunction

Timer-triggered Azure Function (isolated worker, .NET 10) that hard-deletes Cosmos **Users** who have had no `OwnedHomeIds` for longer than a configurable number of days. Uses shared logic from the **Web** project (`UserPurgeRunner`, `CosmosUserRepository`).

**Note:** `Microsoft.Azure.Functions.Worker.Sdk` **2.0.5+** is required so `net10.0` is accepted by the Functions build targets.

## Configuration

Set these in **Azure App Settings** (or `local.settings.json` → `Values` for local):

| Setting | Description |
|--------|----------------|
| `CosmosUri` | Cosmos account endpoint (same as web app). |
| `CosmosKey` | Access key, or use RBAC + managed identity in a future iteration. |
| `CosmosDatabase` | Database name. |
| `UserPurge__Enabled` | `true` to run purge logic; `false` skips work. |
| `UserPurge__DryRun` | `true` logs candidates only; no audit write or delete. |
| `UserPurge__PurgeAfterDays` | Minimum days unassociated (default `30`). |
| `UserPurge__MaxDeletesPerRun` | Cap per timer execution (default `100`). |
| `UserPurgeSchedule` | NCRONTAB in UTC, 6 fields, e.g. `0 0 10 * * *` (10:00 daily). Use this exact key (not `UserPurge__Schedule`) so `%UserPurgeSchedule%` resolves in the timer binding. |
| `PayPalSyncSchedule` | PayPal sync timer NCRONTAB, e.g. `0 0 6 * * 1` (Mondays 06:00 UTC). |
| `PayPal__SyncEnabled`, `PayPal__ClientId`, … | See `PayPalSyncTimerFunction` / `PayPalOptions`; nested keys use `__` as usual. |
| `AzureWebJobsStorage` | Required by Functions host (use a real storage account in Azure). |

Users with role **Administrator** are never deleted.

## Local run

1. Fill `CosmosUri`, `CosmosKey`, and `CosmosDatabase` in `local.settings.json` (and/or user-secrets; see `Program.cs`).
2. **Storage emulator (required for timer triggers):** The default `AzureWebJobsStorage` value `UseDevelopmentStorage=true` points at **Azurite** on `127.0.0.1` (blob **10000**, queue **10001**, table **10002**). If nothing is listening there, the host logs *connection refused* and timer listeners fail to start. Either:
   - Start Azurite before `func start`, e.g. `npx azurite` (or the Azurite VS Code extension), or  
   - Replace `AzureWebJobsStorage` with a **real** Azure Storage connection string (any cheap dev storage account is fine).
3. From this folder: `func start` (Azure Functions Core Tools) or run/debug from Visual Studio.

## Deployment

- Deploy as a separate **Function App** from the web app.
- Prefer **Key Vault references** for `CosmosKey` and enable **Application Insights** on the Function App for operational logs.

### GitHub Actions

If you use the repo workflow `.github/workflows/master_userpurgefunction.yml`, create these repository secrets:

- `AzureFunctionApp_Name_UserPurge`: the Azure Function App name (e.g. `my-funcapp`)
- `AzureFunctionApp_PublishProfile_UserPurge`: the XML publish profile contents from the Function App
