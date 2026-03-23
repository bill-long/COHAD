using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Web.Services.Repositories;
using CosmosClient = Microsoft.Azure.Cosmos.CosmosClient;

namespace Web.IntegrationTests.Support;

/// <summary>
/// Shared Cosmos setup: optional database + container provisioning (matching legacy EF "NoPartitionKey" layout),
/// then repository factories for integration tests.
/// </summary>
public sealed class CosmosIntegrationFixture : IAsyncLifetime
{
    private CosmosClient? _client;

    public bool IsAvailable { get; private set; }
    public string UnavailableReason { get; private set; } = "";

    public CosmosTestSettings Settings { get; private set; } = new();

    public CosmosClient Client =>
        _client ?? throw new InvalidOperationException("Cosmos client is not initialized. Was the fixture started?");

    public string DatabaseId => Settings.DatabaseName;

    public IUserRepository CreateUserRepository() =>
        new CosmosUserRepository(Client.GetContainer(DatabaseId, "Users"));

    public IHomeRepository CreateHomeRepository() =>
        new CosmosHomeRepository(Client.GetContainer(DatabaseId, "Homes"));

    public IAuditLogRepository CreateAuditLogRepository() =>
        new CosmosAuditLogRepository(
            Client.GetContainer(DatabaseId, "AuditLog"),
            NullLogger<CosmosAuditLogRepository>.Instance);

    public IPaymentRepository CreatePaymentRepository() =>
        new CosmosPaymentRepository(
            Client.GetContainer(DatabaseId, "Payments"),
            CreateHomeRepository(),
            CreateUserRepository());

    public async Task InitializeAsync()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.CosmosTests.json", optional: false, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();

        Settings = config.GetSection("CosmosTests").Get<CosmosTestSettings>() ?? new CosmosTestSettings();

        var runFlag = Environment.GetEnvironmentVariable("RUN_COSMOS_INTEGRATION_TESTS");
        if (runFlag is "1" or "true" or "True")
        {
            Settings.Enabled = true;
        }

        if (!Settings.Enabled)
        {
            IsAvailable = false;
            UnavailableReason =
                "Cosmos integration tests are disabled. Set RUN_COSMOS_INTEGRATION_TESTS=1 (forces enable) " +
                "or set CosmosTests:Enabled to true in appsettings / CosmosTests__Enabled environment variable.";
            return;
        }

        if (string.Equals(Settings.Mode, "Account", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(Settings.AccountUri) || string.IsNullOrWhiteSpace(Settings.AccountKey))
            {
                IsAvailable = false;
                UnavailableReason =
                    "CosmosTests:Mode is Account but AccountUri / AccountKey are empty. Set CosmosTests__AccountUri and " +
                    "CosmosTests__AccountKey, or use Mode=Emulator.";
                return;
            }
        }

        try
        {
            var useEmulator = !string.Equals(Settings.Mode, "Account", StringComparison.OrdinalIgnoreCase);
            var endpoint = useEmulator ? Settings.EmulatorUri : Settings.AccountUri;
            var key = useEmulator ? Settings.EmulatorKey : Settings.AccountKey;

            var options = useEmulator ? CreateEmulatorClientOptions() : new CosmosClientOptions();
            _client = new CosmosClient(endpoint, key, options);

            if (Settings.AutoProvision)
            {
                await ProvisionAsync().ConfigureAwait(false);
            }

            IsAvailable = true;
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            UnavailableReason = $"Cosmos setup failed: {ex.Message}";
        }
    }

    private async Task ProvisionAsync()
    {
        if (_client == null)
        {
            throw new InvalidOperationException("Client not created.");
        }

        Database database;
        if (Settings.DatabaseThroughputRu is int ru && ru > 0)
        {
            database = await _client.CreateDatabaseIfNotExistsAsync(
                    Settings.DatabaseName,
                    ThroughputProperties.CreateManualThroughput(ru))
                .ConfigureAwait(false);
        }
        else
        {
            database = await _client.CreateDatabaseIfNotExistsAsync(Settings.DatabaseName).ConfigureAwait(false);
        }

        var pkPath = Settings.PartitionKeyPath?.Trim();
        if (string.IsNullOrEmpty(pkPath))
        {
            throw new InvalidOperationException("CosmosTests:PartitionKeyPath is required when AutoProvision is true.");
        }

        var containerNames = new[] { "Users", "Homes", "Payments", "AuditLog" };
        foreach (var name in containerNames)
        {
            await database.CreateContainerIfNotExistsAsync(new ContainerProperties(name, pkPath)).ConfigureAwait(false);
        }
    }

    private static CosmosClientOptions CreateEmulatorClientOptions()
    {
        return new CosmosClientOptions
        {
            ConnectionMode = ConnectionMode.Gateway,
            HttpClientFactory = () =>
            {
                var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback =
                        HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                };
                return new HttpClient(handler);
            }
        };
    }

    public Task DisposeAsync()
    {
        if (_client != null)
        {
            _client.Dispose();
            _client = null;
        }

        return Task.CompletedTask;
    }
}
