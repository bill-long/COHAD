# Web.UnitTests

Fast **unit tests** (no Cosmos): legacy JSON mapping, claims parsing, presentation models, `HomeController.Update` branches (mocked repositories), and `RoleAuthorizationHandler`.

`Web` exposes `internal` types to this project via `InternalsVisibleTo` so `CosmosLegacyDocumentMapper` can be covered directly.

```powershell
dotnet test .\Web.UnitTests\Web.UnitTests.csproj
```

See `Web.IntegrationTests` for Cosmos emulator / test-account integration tests.
