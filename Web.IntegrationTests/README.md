# Web.IntegrationTests

For fast tests without Cosmos, see **`Web.UnitTests`**.

xUnit integration tests against **Azure Cosmos DB** (emulator or a dedicated test account). These catch issues like **partition key mismatches** that only show up on real `CreateItem` / `UpsertItem` calls.

## When tests run

By default, Cosmos tests are **skipped** so `dotnet test` does not require Cosmos.

To enable:

1. Set **`RUN_COSMOS_INTEGRATION_TESTS=1`** (recommended), **or** set `CosmosTests:Enabled` to `true` in `appsettings.CosmosTests.json`, **or** set environment variable `CosmosTests__Enabled=true`.

2. Start the **Cosmos Emulator** *or* configure **Mode=Account** with URI + key (see below).

Then:

```powershell
$env:RUN_COSMOS_INTEGRATION_TESTS = "1"
dotnet test .\Web.IntegrationTests\Web.IntegrationTests.csproj
```

## Emulator (local)

1. Install and start the [Azure Cosmos DB Emulator](https://learn.microsoft.com/azure/cosmos-db/how-to-develop-emulator).
2. Keep `CosmosTests:Mode` = `Emulator` in `appsettings.CosmosTests.json` (default).
3. The file already contains the well-known emulator key and `https://localhost:8081`.

Emulator uses a self-signed certificate; the fixture configures the Cosmos client to accept it (**Gateway** mode).

## Test account (Azure)

Use a **non-production** database. Containers must use the **same partition key definition** as production (see Portal → container → **Partition key**).

1. Set environment variables (do not commit secrets):

   ```text
   CosmosTests__Mode=Account
   CosmosTests__AccountUri=https://your-account.documents.azure.com:443/
   CosmosTests__AccountKey=...
   CosmosTests__DatabaseName=cohad-integration
   ```

2. If containers already exist (cloned from prod or Bicep/Terraform), set **`CosmosTests__AutoProvision`** to `false` so the tests only connect and run CRUD.

3. If **`AutoProvision`** is `true`, the fixture creates the database (optional 400 RU/s) and `Users`, `Homes`, `Payments`, `AuditLog` with **`CosmosTests:PartitionKeyPath`**. This must match prod (often `/NoPartitionKey` in the portal for legacy EF layouts; if provisioning fails, try `/__partitionKey`).

## Partition key path

If tests fail with **PartitionKeyMismatch** against your test account:

- Open **Data Explorer** → container → **Settings** and copy the **partition key path** exactly into `CosmosTests__PartitionKeyPath` or `appsettings.CosmosTests.json`.

## dotnet test without Cosmos

Omit `RUN_COSMOS_INTEGRATION_TESTS`; tests skip with a clear reason.
