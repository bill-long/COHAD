using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using CosmosClient = Microsoft.Azure.Cosmos.CosmosClient;
using Web.Services;
using Web.Services.Repositories;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureAppConfiguration(builder => builder.AddEnvironmentVariables())
    .ConfigureServices((context, services) =>
    {
        var config = context.Configuration;
        var uri = config["CosmosUri"];
        var key = config["CosmosKey"];
        var db = config["CosmosDatabase"];
        if (string.IsNullOrWhiteSpace(uri) || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(db))
        {
            throw new InvalidOperationException(
                "CosmosUri, CosmosKey, and CosmosDatabase must be configured (environment variables or Azure App Settings).");
        }

        services.AddSingleton(_ => new CosmosClient(uri, key));
        services.AddSingleton<IUserRepository>(sp =>
            new CosmosUserRepository(sp.GetRequiredService<CosmosClient>().GetContainer(db, "Users")));
        services.AddSingleton<IAuditLogRepository>(sp =>
            new CosmosAuditLogRepository(sp.GetRequiredService<CosmosClient>().GetContainer(db, "AuditLog")));
        services.AddSingleton<UserPurgeRunner>();
    })
    .Build();

host.Run();
