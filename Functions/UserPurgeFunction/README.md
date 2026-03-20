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
| `UserPurge__Schedule` | NCRONTAB in UTC, 6 fields, e.g. `0 0 10 * * *` (10:00 daily). |
| `AzureWebJobsStorage` | Required by Functions host (use a real storage account in Azure). |

Users with role **Administrator** are never deleted.

## Local run

1. Fill `CosmosUri`, `CosmosKey`, and `CosmosDatabase` in `local.settings.json`.
2. Start Azurite or provide a valid `AzureWebJobsStorage` connection string.
3. From this folder: `func start` (Azure Functions Core Tools) or run/debug from Visual Studio.

## Deployment

- Deploy as a separate **Function App** from the web app.
- Prefer **Key Vault references** for `CosmosKey` and enable **Application Insights** on the Function App for operational logs.

### GitHub Actions

If you use the repo workflow `.github/workflows/master_userpurgefunction.yml`, create these repository secrets:

- `AzureFunctionApp_Name_UserPurge`: the Azure Function App name (e.g. `my-funcapp`)
- `AzureFunctionApp_PublishProfile_UserPurge`: the XML publish profile contents from the Function App
