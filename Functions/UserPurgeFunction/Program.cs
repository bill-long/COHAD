using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UserPurgeFunction;
using Web.Services;
using Web.Services.Repositories;
using CosmosClient = Microsoft.Azure.Cosmos.CosmosClient;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureAppConfiguration(builder =>
    {
        // Core Tools merges local.settings.json Values into the environment first (often empty placeholders).
        builder.AddEnvironmentVariables();
        // User-secrets load second so real values win over blank CosmosUri/CosmosKey/CosmosDatabase in local.settings.
        // In Azure, this provider typically adds nothing; App Settings from the environment remain.
        builder.AddUserSecrets(typeof(PurgeTimerFunction).Assembly, optional: true);
    })
    .ConfigureServices(
        (context, services) =>
        {
            var config = context.Configuration;
            var uri = config["CosmosUri"];
            var key = config["CosmosKey"];
            var db = config["CosmosDatabase"];
            if (string.IsNullOrWhiteSpace(uri) || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(db))
            {
                throw new InvalidOperationException(
                    "CosmosUri, CosmosKey, and CosmosDatabase must be configured (environment variables or Azure App Settings)."
                );
            }

            services.Configure<PayPalOptions>(config.GetSection("PayPal"));
            services.AddHttpClient<PayPalTransactionSearchClient>();
            // Align with typed HttpClient lifetime: do not capture a transient client in a singleton runner.
            services.AddTransient<PayPalPaymentSyncRunner>();

            services.AddSingleton(_ => new CosmosClient(uri, key));
            services.AddSingleton<IUserRepository>(sp => new CosmosUserRepository(
                sp.GetRequiredService<CosmosClient>().GetContainer(db, "Users")
            ));
            services.AddSingleton<IHomeRepository>(sp => new CosmosHomeRepository(
                sp.GetRequiredService<CosmosClient>().GetContainer(db, "Homes")
            ));
            services.AddSingleton<IResidentRepository>(sp => new CosmosResidentRepository(
                sp.GetRequiredService<CosmosClient>().GetContainer(db, "Residents")
            ));
            services.AddSingleton<IAuditLogRepository>(sp => new CosmosAuditLogRepository(
                sp.GetRequiredService<CosmosClient>().GetContainer(db, "AuditLog"),
                sp.GetRequiredService<ILogger<CosmosAuditLogRepository>>()
            ));
            services.AddSingleton<IPaymentRepository>(sp => new CosmosPaymentRepository(
                sp.GetRequiredService<CosmosClient>().GetContainer(db, "Payments"),
                sp.GetRequiredService<IHomeRepository>(),
                sp.GetRequiredService<IResidentRepository>(),
                sp.GetRequiredService<IUserRepository>()
            ));
            services.AddSingleton<UserPurgeRunner>();
        }
    )
    .Build();

host.Run();
