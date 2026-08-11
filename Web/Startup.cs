using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Web.Authorization;
using Web.Configuration;
using Web.Hubs;
using Web.MockData;
using Web.Models;
using Web.Services;
using Web.Services.Repositories;
using CosmosClient = Microsoft.Azure.Cosmos.CosmosClient;

namespace Web
{
    public class Startup
    {
        public Startup(IConfiguration configuration, IWebHostEnvironment environment)
        {
            Configuration = configuration;
            Environment = environment;
        }

        public IConfiguration Configuration { get; }

        /// <summary>
        /// Configuration problems detected while registering services, logged as Errors the moment
        /// the logging pipeline exists in <see cref="Configure"/>. ConfigureServices has no logger,
        /// and deferring a message to a service's first resolution means the deploy-verification
        /// log check shows nothing until a user has already hit the misconfiguration.
        /// </summary>
        private readonly List<string> _startupConfigurationErrors = new();

        public IWebHostEnvironment Environment { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            var aiConnectionString = Configuration["ApplicationInsights:ConnectionString"];
            if (!string.IsNullOrEmpty(aiConnectionString))
            {
                services.AddApplicationInsightsTelemetry();
            }

            services.AddControllers();
            services.AddSignalR();
            services.AddMemoryCache();
            services.AddScoped<DocumentListCache>(sp => new DocumentListCache(
                sp.GetRequiredService<IDocumentRepository>(),
                sp.GetRequiredService<IDocumentFolderRepository>(),
                sp.GetRequiredService<IMemoryCache>(),
                sp.GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>().Value.JsonSerializerOptions
            ));
            services.AddScoped<CommitteeListCache>(sp => new CommitteeListCache(
                sp.GetRequiredService<ICommitteeRepository>(),
                sp.GetRequiredService<IMemoryCache>(),
                sp.GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>().Value.JsonSerializerOptions
            ));
            services.AddScoped<ResidentCleanupService>();

            services.AddResponseCompression(options =>
            {
                // BREACH/CRIME risk is mitigated: the app uses JWT Bearer auth (no
                // cookie-based CSRF tokens) and compressed responses do not mix
                // secret values with attacker-controlled content.
                options.EnableForHttps = true;
                options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(
                    new[] { "application/json", "image/svg+xml" }
                );
            });
            services.Configure<BrotliCompressionProviderOptions>(options =>
            {
                options.Level = System.IO.Compression.CompressionLevel.Fastest;
            });

            // In production, the Angular files will be served from this directory
            services.AddSpaStaticFiles(configuration =>
            {
                configuration.RootPath = "ClientApp/dist/cohad-app";
            });

            // Restrict cross-origin requests to the same origin by default.
            // The SPA is served by this same host, so no additional origins need to be permitted here.
            services.AddCors(options =>
            {
                options.AddDefaultPolicy(policy =>
                {
                    policy
                        .WithOrigins(
                            Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? Array.Empty<string>()
                        )
                        .AllowAnyHeader()
                        .AllowAnyMethod();
                });
            });

            var useMockData = Environment.IsEnvironment("MockData");
            services.Configure<DocumentStorageOptions>(Configuration.GetSection("DocumentStorage"));
            services.Configure<UnsubscribeTokenOptions>(Configuration.GetSection("UnsubscribeToken"));
            // Keep multipart request cap aligned with DocumentStorage:MaxUploadBytes (DocumentController enforces the same limit).
            services.Configure<FormOptions>(options =>
            {
                var max =
                    Configuration.GetSection("DocumentStorage").GetValue<long?>("MaxUploadBytes")
                    ?? new DocumentStorageOptions().MaxUploadBytes;
                options.MultipartBodyLengthLimit = max;
            });

            services
                .AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
                })
                .AddJwtBearer(options =>
                {
                    if (useMockData)
                    {
                        var signingKey = MockJwtSigningKey.ResolveValidated(Configuration);

                        options.TokenValidationParameters = new TokenValidationParameters
                        {
                            ValidateAudience = true,
                            ValidateIssuer = true,
                            ValidateIssuerSigningKey = true,
                            ValidIssuer = MockJwtIssuer.Issuer,
                            ValidAudience = MockJwtIssuer.Audience,
                            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
                        };
                    }
                    else
                    {
                        options.TokenValidationParameters = new TokenValidationParameters
                        {
                            ValidateAudience = true,
                            ValidateIssuer = true,
                            ValidIssuer = "https://cohadorgb2c.b2clogin.com/a7e9006b-c606-4670-960c-3998b35ea5ee/v2.0/",
                            ValidAudience = "5803d9fa-a62f-401c-b0f4-269b3cb468eb",
                        };

                        options.MetadataAddress =
                            "https://cohadorgb2c.b2clogin.com/cohadorgb2c.onmicrosoft.com/b2c_1_default/v2.0/.well-known/openid-configuration";
                    }

                    // SignalR WebSockets cannot send Authorization headers; the JS client passes the JWT via ?access_token=...
                    options.Events = new JwtBearerEvents
                    {
                        OnMessageReceived = context =>
                        {
                            var accessToken = context.Request.Query["access_token"];
                            if (
                                !string.IsNullOrEmpty(accessToken)
                                && context.HttpContext.Request.Path.StartsWithSegments("/hubs")
                            )
                            {
                                context.Token = accessToken;
                            }

                            return Task.CompletedTask;
                        },
                    };
                });

            // Allow reverse proxy from nginx
            services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            });

            // Authorization stuff - make sure users have required roles
            services.AddAuthorization(options =>
            {
                options.DefaultPolicy = new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                    .RequireClaim("http://schemas.microsoft.com/identity/claims/scope", "API")
                    .Build();

                // Role hierarchy: new Administrators are also assigned Resident in UserController.UpdateUserAssociations.
                // RoleAuthorizationHandler additionally treats Administrator as satisfying the Resident policy so legacy
                // admin-only accounts still pass Resident-gated APIs. Do not add redundant OR checks on controllers.
                options.AddPolicy(
                    "Resident",
                    policy => policy.Requirements.Add(new RoleAuthorizationRequirement(User.Role.Resident))
                );
                options.AddPolicy(
                    "Administrator",
                    policy => policy.Requirements.Add(new RoleAuthorizationRequirement(User.Role.Administrator))
                );
                options.AddPolicy(
                    "WelcomeCommittee",
                    policy => policy.Requirements.Add(new RoleAuthorizationRequirement(User.Role.WelcomeCommittee))
                );
                options.AddPolicy(
                    "GardenClub",
                    policy => policy.Requirements.Add(new RoleAuthorizationRequirement(User.Role.GardenClub))
                );
                options.AddPolicy(
                    "Board",
                    policy => policy.Requirements.Add(new RoleAuthorizationRequirement(User.Role.Board))
                );
                options.AddPolicy(
                    "SocialCommittee",
                    policy => policy.Requirements.Add(new RoleAuthorizationRequirement(User.Role.SocialCommittee))
                );
                options.AddPolicy(
                    "SunshineCommittee",
                    policy => policy.Requirements.Add(new RoleAuthorizationRequirement(User.Role.SunshineCommittee))
                );
                options.AddPolicy(
                    "ArchitecturalCommittee",
                    policy =>
                        policy.Requirements.Add(new RoleAuthorizationRequirement(User.Role.ArchitecturalCommittee))
                );
                options.AddPolicy(
                    "LandscapeCommittee",
                    policy => policy.Requirements.Add(new RoleAuthorizationRequirement(User.Role.LandscapeCommittee))
                );

                // Every committee role plus Administrator - used for email job management endpoints and the
                // SignalR hub. Not the same as being able to send: the from-* endpoints have their own
                // per-committee policies, and two of these roles have no mailbox to send as.
                options.AddPolicy(
                    "EmailSender",
                    policy =>
                        policy.Requirements.Add(
                            new AnyRoleAuthorizationRequirement(AuthorizationRoleSets.EmailSender.ToArray())
                        )
                );

                // Any role that can manage at least one committee — used for committee admin endpoints.
                // Keep this set in sync with the frontend `rolePermissions.manageCommitteesRoles`
                // (Web/ClientApp/src/app/services/rolepermission.service.ts); they gate the same feature
                // (committee admin + held-message notifications) on the server and client respectively.
                options.AddPolicy(
                    "CommitteeEditor",
                    policy =>
                        policy.Requirements.Add(
                            new AnyRoleAuthorizationRequirement(AuthorizationRoleSets.CommitteeEditor.ToArray())
                        )
                );
            });

            services.AddScoped<IAuthorizationHandler, RoleAuthorizationHandler>();
            services.AddScoped<IAuthorizationHandler, AnyRoleAuthorizationHandler>();

            // Scoped is the whole point: authorization and the endpoint that follows it share one
            // read of the caller's user document per request.
            services.AddScoped<ICurrentUserAccessor, CurrentUserAccessor>();
            services.AddSingleton<IOgThumbnailService, SkiaSharpOgThumbnailService>();
            services.AddSingleton<IImageConversionService, SkiaSharpImageConversionService>();
            services.AddSingleton<IImageUploadHelper, ImageUploadHelper>();
            services.AddScoped<IEventSignupConversionService, EventSignupConversionService>();

            // Legacy unsubscribe token validation - the real service only when a usable key is
            // configured. Without one, the UnsubscribeController still works: short links resolve
            // through the credential resolver, and a legacy ?token= is rejected as NotConfigured,
            // which the log distinguishes from a bad token.
            //
            // Key selection is UnsubscribeTokenService.SelectSigningKey - the same rule the
            // service's own constructor applies - so the gate here and the constructor cannot
            // drift, which would surface as the constructor throwing inside DI resolution. A key
            // that is PRESENT but too short disables validation and is loudly named at startup
            // (the message is stamped here and logged in Configure, where a logger exists); the
            // silent alternative left a truncated paste at cutover indistinguishable from the
            // deliberate no-key state. Falling back to SigningKey for an invalid LegacySigningKey
            // was considered and rejected: after rotation SigningKey holds a fresh key that cannot
            // validate any legacy link, so that fallback only converts a clear NotConfigured into
            // a misleading DecryptFailed.
            var unsubOptions =
                Configuration.GetSection("UnsubscribeToken").Get<UnsubscribeTokenOptions>()
                ?? new UnsubscribeTokenOptions();
            var (unsubKeyName, unsubKey) = UnsubscribeTokenService.SelectSigningKey(unsubOptions);
            if (UnsubscribeTokenService.IsUsableKey(unsubKey))
            {
                services.AddSingleton<IUnsubscribeTokenService, UnsubscribeTokenService>();
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(unsubKey))
                {
                    _startupConfigurationErrors.Add(
                        $"{unsubKeyName} is set but shorter than {UnsubscribeTokenService.MinKeyBytes} UTF-8 bytes; "
                            + "legacy unsubscribe token validation is DISABLED and every legacy link will be rejected as NotConfigured until the key is fixed."
                    );
                }

                services.AddSingleton<IUnsubscribeTokenService, NullUnsubscribeTokenService>();
            }

            // Issues the short links that replace the long ?token= credential in outgoing mail.
            // Scoped, because it writes through the scoped link repository.
            services.AddScoped<IUnsubscribeLinkIssuer, UnsubscribeLinkIssuer>();

            // The single place that turns any presented credential shape into one payload. Scoped
            // for the same reason - it reads through the scoped link repository.
            services.AddScoped<IUnsubscribeCredentialResolver, UnsubscribeCredentialResolver>();

            // The single place that mutates suppression records - both writers and the admin
            // actions converge here so the record lifecycle is defined once. Scoped because it
            // writes through the scoped suppression repository.
            services.AddScoped<IEmailSuppressionService, EmailSuppressionService>();

            // Bounds how many unsubscribe rejection warnings the [AllowAnonymous] endpoints can
            // write per fixed 24h window. Singleton because the count is shared across requests;
            // in-memory and non-durable on purpose (see IUnsubscribeWarningBudget).
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<IUnsubscribeWarningBudget, UnsubscribeWarningBudget>();

            // Repository / persistence
            if (useMockData)
            {
                services.AddSingleton<IUserRepository, MockUserRepository>();
                services.AddSingleton<IHomeRepository, MockHomeRepository>();
                services.AddSingleton<IPaymentRepository>(sp => new MockPaymentRepository(
                    sp.GetRequiredService<IHomeRepository>(),
                    sp.GetRequiredService<IResidentRepository>(),
                    sp.GetRequiredService<IUserRepository>()
                ));
                services.AddSingleton<IAuditLogRepository, MockAuditLogRepository>();
                services.AddSingleton<IDocumentRepository, MockDocumentRepository>();
                services.AddSingleton<IDocumentFolderRepository, MockDocumentFolderRepository>();
                services.AddSingleton<ICommunityEventRepository, MockCommunityEventRepository>();
                services.AddSingleton<IBlogPostRepository, MockBlogPostRepository>();
                services.AddSingleton<IBlogCommentRepository, MockBlogCommentRepository>();
                services.AddSingleton<IVendorRepository, MockVendorRepository>();
                services.AddSingleton<IVendorReviewRepository, MockVendorReviewRepository>();
                services.AddSingleton<IVendorFlagRepository, MockVendorFlagRepository>();
                services.AddSingleton<IYouthServiceListingRepository, MockYouthServiceListingRepository>();
                services.AddSingleton<ICommitteeRepository, MockCommitteeRepository>();
                services.AddSingleton<IResidentRepository, MockResidentRepository>();
                services.AddSingleton<IDocumentFileStore>(sp => new CachedDocumentFileStore(
                    new MockDocumentFileStore()
                ));
                // Seeds sample completed jobs + HTML blobs for Manage → Email testing.
                services.AddSingleton<IEmailJobRepository>(sp => new MockEmailJobRepository(
                    sp.GetRequiredService<IDocumentFileStore>()
                ));
                services.AddSingleton<IEmailDeliveryEventRepository, MockEmailDeliveryEventRepository>();
                // Seeded with a held message so the Approvals inbox and the approve flow's
                // suppression-aware recipient selection are exercisable; see SeedSampleData.
                services.AddSingleton<IHeldMessageRepository>(
                    new MockHeldMessageRepository().SeedSampleData()
                );
                services.AddSingleton<INotificationRepository, MockNotificationRepository>();
                services.AddSingleton<INotificationDigestStateRepository, MockNotificationDigestStateRepository>();
                services.AddSingleton<IBackgroundJobStateRepository, MockBackgroundJobStateRepository>();
                services.AddSingleton<IUnsubscribeLinkRepository, MockUnsubscribeLinkRepository>();
                // Seeded so the Suppressions page, the job-detail explanation, and the send-path
                // skip are all exercisable in MockData; see SeedSampleData.
                services.AddSingleton<IEmailSuppressionRepository>(
                    new MockEmailSuppressionRepository().SeedSampleData()
                );
                // Seeded with one dumped address holding no COHAD suppression, so the suppression
                // sync's first run records it end to end (and clearing that record exercises the
                // provider-side reactivation, which deletes the mock dump entry); see
                // SeedSampleData.
                services.AddSingleton<IPostmarkSuppressionClient>(
                    new MockPostmarkSuppressionClient().SeedSampleData()
                );
            }
            else
            {
                var uri = Configuration["CosmosUri"];
                var key = Configuration["CosmosKey"];
                var db = Configuration["CosmosDatabase"];

                services.AddSingleton(_ => new CosmosClient(
                    uri,
                    key,
                    new Microsoft.Azure.Cosmos.CosmosClientOptions
                    {
                        ConnectionMode = Microsoft.Azure.Cosmos.ConnectionMode.Direct,
                        MaxRetryAttemptsOnRateLimitedRequests = 9,
                        MaxRetryWaitTimeOnRateLimitedRequests = TimeSpan.FromSeconds(30),
                        CosmosClientTelemetryOptions = new Microsoft.Azure.Cosmos.CosmosClientTelemetryOptions
                        {
                            DisableDistributedTracing = false,
                        },
                    }
                ));
                services.AddScoped<IUserRepository>(sp => new CosmosUserRepository(
                    sp.GetRequiredService<CosmosClient>().GetContainer(db, "Users")
                ));
                services.AddScoped<IHomeRepository>(sp => new CosmosHomeRepository(
                    sp.GetRequiredService<CosmosClient>().GetContainer(db, "Homes")
                ));
                services.AddScoped<IPaymentRepository>(sp => new CosmosPaymentRepository(
                    sp.GetRequiredService<CosmosClient>().GetContainer(db, "Payments"),
                    sp.GetRequiredService<IHomeRepository>(),
                    sp.GetRequiredService<IResidentRepository>(),
                    sp.GetRequiredService<IUserRepository>()
                ));
                services.AddScoped<IAuditLogRepository>(sp => new CosmosAuditLogRepository(
                    sp.GetRequiredService<CosmosClient>().GetContainer(db, "AuditLog"),
                    sp.GetRequiredService<ILogger<CosmosAuditLogRepository>>()
                ));
                services.AddScoped<IDocumentRepository>(sp => new CosmosDocumentRepository(
                    sp.GetRequiredService<CosmosClient>().GetContainer(db, "Documents")
                ));
                services.AddScoped<IDocumentFolderRepository>(sp => new CosmosDocumentFolderRepository(
                    sp.GetRequiredService<CosmosClient>().GetContainer(db, "Documents")
                ));
                services.AddScoped<ICommunityEventRepository>(sp => new CosmosCommunityEventRepository(
                    sp.GetRequiredService<CosmosClient>().GetContainer(db, "Events")
                ));
                services.AddScoped<IBlogPostRepository>(sp => new CosmosBlogPostRepository(
                    sp.GetRequiredService<CosmosClient>().GetContainer(db, "BlogPosts")
                ));
                services.AddScoped<IBlogCommentRepository>(sp => new CosmosBlogCommentRepository(
                    sp.GetRequiredService<CosmosClient>().GetContainer(db, "BlogComments")
                ));
                services.AddScoped<IVendorRepository>(sp => new CosmosVendorRepository(
                    sp.GetRequiredService<CosmosClient>().GetContainer(db, "Vendors")
                ));
                services.AddScoped<IVendorReviewRepository>(sp => new CosmosVendorReviewRepository(
                    sp.GetRequiredService<CosmosClient>().GetContainer(db, "VendorReviews")
                ));
                services.AddScoped<IVendorFlagRepository>(sp => new CosmosVendorFlagRepository(
                    sp.GetRequiredService<CosmosClient>().GetContainer(db, "VendorFlags")
                ));
                services.AddScoped<IYouthServiceListingRepository>(sp => new CosmosYouthServiceListingRepository(
                    sp.GetRequiredService<CosmosClient>().GetContainer(db, "YouthServices")
                ));
                services.AddScoped<ICommitteeRepository>(sp => new CosmosCommitteeRepository(
                    sp.GetRequiredService<CosmosClient>().GetContainer(db, "Committees")
                ));
                services.AddScoped<IResidentRepository>(sp => new CosmosResidentRepository(
                    sp.GetRequiredService<CosmosClient>().GetContainer(db, "Residents")
                ));
                services.AddSingleton<IDocumentFileStore>(sp => new CachedDocumentFileStore(
                    new AzureBlobDocumentFileStore(sp.GetRequiredService<IOptions<DocumentStorageOptions>>())
                ));

                services.AddScoped<IEmailJobRepository>(sp => new CosmosEmailJobRepository(
                    sp.GetRequiredService<CosmosClient>().GetContainer(db, "EmailJobs")
                ));

                services.AddScoped<IEmailDeliveryEventRepository>(sp => new CosmosEmailDeliveryEventRepository(
                    sp.GetRequiredService<CosmosClient>().GetContainer(db, "EmailDeliveryEvents")
                ));

                services.AddScoped<IHeldMessageRepository>(sp => new CosmosHeldMessageRepository(
                    sp.GetRequiredService<CosmosClient>().GetContainer(db, "HeldMessages")
                ));

                services.AddScoped<INotificationRepository>(sp => new CosmosNotificationRepository(
                    sp.GetRequiredService<CosmosClient>().GetContainer(db, "Notifications")
                ));

                services.AddScoped<INotificationDigestStateRepository>(sp => new CosmosNotificationDigestStateRepository(
                    sp.GetRequiredService<CosmosClient>().GetContainer(db, "NotificationDigestState")
                ));

                services.AddScoped<IBackgroundJobStateRepository>(sp => new CosmosBackgroundJobStateRepository(
                    sp.GetRequiredService<CosmosClient>().GetContainer(db, "BackgroundJobState")
                ));

                // Provisioned out of band like every container here, with a ~400 day TTL. The link
                // lifetime itself is enforced in code (UnsubscribeLink.MaxLinkAge) - the TTL only
                // prunes.
                services.AddScoped<IUnsubscribeLinkRepository>(sp => new CosmosUnsubscribeLinkRepository(
                    sp.GetRequiredService<CosmosClient>().GetContainer(db, "UnsubscribeLink")
                ));

                // Provisioned out of band (non-partitioned, /NoPartitionKey) with NO TTL: a
                // suppression is permanent until a human clears it, and a row that quietly expired
                // would resume mail to an address that bounced or complained. If the container is
                // missing, the enforcement point fails the job rather than sending unfiltered.
                services.AddScoped<IEmailSuppressionRepository>(sp => new CosmosEmailSuppressionRepository(
                    sp.GetRequiredService<CosmosClient>().GetContainer(db, "EmailSuppression")
                ));

                // Graph API for committee mailbox forwarding — registered only when credentials are configured.
                var graphTenantId = Configuration["Graph:TenantId"];
                var graphClientId = Configuration["Graph:ClientId"];
                var graphClientSecret = Configuration["Graph:ClientSecret"];
                var graphConfigured =
                    !string.IsNullOrWhiteSpace(graphTenantId)
                    && !string.IsNullOrWhiteSpace(graphClientId)
                    && !string.IsNullOrWhiteSpace(graphClientSecret);

                if (graphConfigured)
                {
                    services.AddSingleton<IGraphMailReader, GraphMailReader>();
                }
            }

            // Notification service (shared across environments). The realtime notifier broadcasts a
            // detail-free "changed" signal over SignalR (NotificationsHub) so connected clients re-fetch
            // the authorized list; a failed signal never fails the persisted change (see NotificationService).
            services.AddSingleton<INotificationRealtimeNotifier, SignalRNotificationRealtimeNotifier>();
            services.AddScoped<INotificationService, NotificationService>();

            // Notification escalation: a background sweep turns aged, unresolved in-app notifications into
            // throttled email digests. Gated by NotificationEscalation:Enabled (the service self-disables
            // when off). The runner is scoped; the hosted service creates a scope per sweep.
            services.Configure<NotificationEscalationOptions>(
                Configuration.GetSection(NotificationEscalationOptions.SectionName)
            );
            services.AddScoped<INotificationRecipientResolver, NotificationRecipientResolver>();
            services.AddScoped<NotificationEscalationRunner>();
            services.AddHostedService<NotificationEscalationService>();

            // Scheduled maintenance jobs. These previously ran as timer-triggered Azure Functions in a
            // separate Function App; they are hosted in-process so there is one deployable and no
            // dependency on the Functions runtime. Both self-disable when their Enabled flag is off.
            //
            // Only the PayPal sync paces from persisted state (the out-of-band BackgroundJobState
            // container), because its interval is longer than the app's typical uptime between deploys, so
            // an in-process timer would rarely reach the next occurrence. The purge needs none: its sweep
            // is unbounded, so running more often than configured is free and running less often is
            // harmless. See UserPurgeService's remarks before adding pacing state back.
            services.Configure<UserPurgeOptions>(Configuration.GetSection("UserPurge"));
            services.Configure<PayPalOptions>(Configuration.GetSection("PayPal"));
            if (useMockData)
            {
                // Never let a mock run reach the live PayPal API, whatever the config says. Mirrors the
                // PostConfigure used for PostmarkOptions below.
                services.PostConfigure<PayPalOptions>(opts => opts.SyncEnabled = false);
            }

            services.AddScoped<UserPurgeRunner>();
            services.AddHttpClient<PayPalTransactionSearchClient>();
            services.AddScoped<IPayPalPaymentSyncRunner, PayPalPaymentSyncRunner>();
            services.AddScoped<PayPalSyncScheduler>();

            // Periodic reconciliation against Postmark's stream suppression dumps (issue #9):
            // catches addresses suppressed at the Postmark layer while the SubscriptionChange
            // webhook trigger was not configured. Self-disables unless
            // Postmark:SuppressionSync:Enabled is set; idempotent via deterministic evidence keys,
            // so the interval is in-process pacing only, like the user purge. The suppression
            // client is an in-memory fake in MockData (registered above); the real one is the
            // codebase's only Postmark HTTP client, scoped to the suppression dump and delete
            // endpoints.
            services.Configure<PostmarkSuppressionSyncOptions>(
                Configuration.GetSection(PostmarkSuppressionSyncOptions.SectionName)
            );
            if (!useMockData)
            {
                // The default HttpClient timeout is 100s; a hung Postmark would otherwise stall
                // the sequential two-stream loop that long per stream, every run.
                services.AddHttpClient<IPostmarkSuppressionClient, PostmarkSuppressionClient>(
                    client => client.Timeout = TimeSpan.FromSeconds(30)
                );
            }
            services.AddScoped<PostmarkSuppressionSyncRunner>();
            // The other consumer of the Postmark suppression API: clearing a ProviderUnsubscribe
            // suppression also reactivates the address at the provider (issue #11), so the
            // two-system clear is one action. Without a server token (webhook-only Postmark, or
            // no Postmark at all) the real client cannot call the API and - more to the point -
            // sends do not pass through Postmark's suppression filter, so a no-op implementation
            // is registered instead of warning every clear about an unresolvable provider
            // failure (the DisabledSpamClassifier precedent). MockData keeps the real service:
            // its suppression client is the in-memory fake, which needs no token.
            if (useMockData || !string.IsNullOrWhiteSpace(Configuration["Postmark:ServerToken"]))
            {
                services.AddScoped<IPostmarkReactivationService, PostmarkReactivationService>();
            }
            else
            {
                services.AddScoped<IPostmarkReactivationService, NotConfiguredPostmarkReactivationService>();
            }
            // Registered only when their data layer can actually work. Without this the loops would run
            // and throw on every tick, contradicting the startup error logged in Configure that says they
            // are not running.
            var jobsHaveDataLayer =
                useMockData
                || (
                    !string.IsNullOrWhiteSpace(Configuration["CosmosUri"])
                    && !string.IsNullOrWhiteSpace(Configuration["CosmosKey"])
                    && !string.IsNullOrWhiteSpace(Configuration["CosmosDatabase"])
                );
            if (jobsHaveDataLayer)
            {
                services.AddHostedService<UserPurgeService>();
                services.AddHostedService<PayPalSyncService>();
                services.AddHostedService<PostmarkSuppressionSyncService>();
            }

            // Email job queue and background processor (shared across environments)
            services.AddSingleton<EmailJobQueue>();
            services.AddSingleton<EmailJobProcessor>();
            services.AddHostedService(sp => sp.GetRequiredService<EmailJobProcessor>());

            // Committee mail poller — polls shared mailboxes and creates forwarding EmailJobs.
            // Only registered when Graph API credentials are configured; CommitteeForwarding:Enabled
            // must also be true for the poller to actually run.
            if (services.Any(sd => sd.ServiceType == typeof(IGraphMailReader)))
            {
                // LLM spam classifier for held (non-directory) committee mail. A real classifier is wired
                // only when an Anthropic API key is configured; otherwise a no-op is registered and the
                // poller falls back to notifying moderators for every held message. Behavior is further
                // gated by CommitteeForwarding:SpamClassification:Enabled, read inside the poller.
                var anthropicApiKey = Configuration["Anthropic:ApiKey"];
                if (!string.IsNullOrWhiteSpace(anthropicApiKey))
                {
                    var spamModel = Configuration.GetValue(
                        "CommitteeForwarding:SpamClassification:Model",
                        AnthropicSpamClassifier.DefaultModel
                    );
                    services.AddSingleton<ISpamClassifier>(sp => new AnthropicSpamClassifier(
                        anthropicApiKey,
                        spamModel,
                        sp.GetRequiredService<ILogger<AnthropicSpamClassifier>>()
                    ));
                }
                else
                {
                    services.AddSingleton<ISpamClassifier, DisabledSpamClassifier>();
                }

                services.AddSingleton<CommitteeMailPoller>();
                services.AddHostedService(sp => sp.GetRequiredService<CommitteeMailPoller>());
            }

            // Email transport abstraction (SMTP / Postmark per-recipient routing)
            services.Configure<PostmarkOptions>(Configuration.GetSection("Postmark"));
            if (useMockData)
            {
                // MockData environment uses MockEmailTransport for everything — force Postmark disabled
                // via PostConfigure so IOptions<PostmarkOptions> consumers see a single source of truth.
                services.PostConfigure<PostmarkOptions>(opts => opts.Enabled = false);
                services.AddSingleton<IEmailTransport>(new MockData.MockEmailTransport());
            }
            else
            {
                services.AddSingleton<IEmailTransport>(sp =>
                {
                    var smtpOptions = new SmtpOptions
                    {
                        SmtpHost = Configuration["SmtpHost"],
                        SmtpUser = Configuration["SmtpUser"],
                        SmtpPassword = Configuration["SmtpPassword"],
                        TimeoutSeconds = Configuration.GetValue("SmtpTimeoutSeconds", 30),
                        MaxIdleSeconds = Configuration.GetValue("SmtpMaxIdleSeconds", 60),
                    };
                    var logProtocol = Configuration.GetValue<bool>("EmailJobs:LogSmtpProtocolOnFailure");
                    return new SmtpEmailTransport(
                        smtpOptions,
                        logProtocol,
                        sp.GetRequiredService<ILogger<SmtpEmailTransport>>()
                    );
                });
            }

            var postmarkOptions = Configuration.GetSection("Postmark").Get<PostmarkOptions>() ?? new PostmarkOptions();
            if (useMockData)
                postmarkOptions.Enabled = false;
            if (postmarkOptions.Enabled && postmarkOptions.UsePostmarkAsDefault)
            {
                if (string.IsNullOrWhiteSpace(postmarkOptions.ServerToken))
                {
                    throw new InvalidOperationException(
                        "Postmark is enabled with UsePostmarkAsDefault, but Postmark:ServerToken is missing or empty. "
                            + "Set it via user secrets or environment variable (Postmark__ServerToken)."
                    );
                }
            }

            services.AddSingleton<EmailTransportRouter>(sp =>
            {
                var defaultTransport = sp.GetRequiredService<IEmailTransport>();
                var opts = sp.GetRequiredService<IOptions<PostmarkOptions>>();
                if (opts.Value.Enabled && opts.Value.UsePostmarkAsDefault)
                {
                    var pmOpts = opts.Value;
                    var logProt = Configuration.GetValue<bool>("EmailJobs:LogSmtpProtocolOnFailure");
                    var pmLogger = sp.GetRequiredService<ILogger<PostmarkEmailTransport>>();
                    var broadcast = new PostmarkEmailTransport(
                        pmOpts.BroadcastSmtpHost,
                        pmOpts.ServerToken,
                        pmOpts.BroadcastStream,
                        pmOpts.TimeoutSeconds,
                        pmOpts.MaxIdleSeconds,
                        logProt,
                        pmLogger
                    );
                    var transactional = new PostmarkEmailTransport(
                        pmOpts.TransactionalSmtpHost,
                        pmOpts.ServerToken,
                        pmOpts.TransactionalStream,
                        pmOpts.TimeoutSeconds,
                        pmOpts.MaxIdleSeconds,
                        logProt,
                        pmLogger
                    );
                    return new EmailTransportRouter(defaultTransport, broadcast, transactional, opts);
                }
                return new EmailTransportRouter(defaultTransport, defaultTransport, defaultTransport, opts);
            });

            // Retention cleanup (invoked on submission; best-effort)
            services.AddScoped<EmailJobCleanupService>();

            // Webhook verification and delivery tracking
            services.Configure<SendGridOptions>(Configuration.GetSection("SendGrid"));
            services.AddSingleton<ISendGridWebhookVerifier, SendGridWebhookVerifier>();
            services.AddSingleton<IPostmarkWebhookVerifier, PostmarkWebhookVerifier>();
            services.AddScoped<IEmailDeliveryActionService, EmailDeliveryActionService>();

            // HttpClientFactory
            services.AddHttpClient();
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, ILogger<Startup> logger)
        {
            // First, so a misconfiguration is in the log during the deploy-verification window
            // rather than after the first affected request.
            foreach (var error in _startupConfigurationErrors)
                logger.LogError("Startup configuration problem: {Problem}", error);

            app.UseForwardedHeaders(
                new ForwardedHeadersOptions
                {
                    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
                }
            );

            // Both scheduled jobs need Cosmos. The web app deliberately starts without Cosmos configured
            // (documented behavior - API calls fail at runtime instead), so unlike the deleted Function
            // host this cannot refuse to start. Say so once at startup instead: otherwise a typo'd
            // CosmosDatabase leaves the site serving traffic normally while the jobs never do their work.
            if (
                !env.IsEnvironment("MockData")
                && (
                    Configuration.GetValue("UserPurge:Enabled", false)
                    || Configuration.GetValue("PayPal:SyncEnabled", false)
                )
                && (
                    string.IsNullOrWhiteSpace(Configuration["CosmosUri"])
                    || string.IsNullOrWhiteSpace(Configuration["CosmosKey"])
                    || string.IsNullOrWhiteSpace(Configuration["CosmosDatabase"])
                )
            )
            {
                logger.LogError(
                    "Scheduled jobs are enabled but CosmosUri/CosmosKey/CosmosDatabase are not all "
                        + "configured, so neither the user purge nor the PayPal sync was started. The "
                        + "PayPal sync additionally requires the out-of-band 'BackgroundJobState' "
                        + "container; the user purge does not."
                );
            }

            var useDevSpaProxy = env.IsDevelopment() || env.IsEnvironment("MockData");

            if (useDevSpaProxy)
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseHsts();

                // Global exception handler: log the error and return a generic 500 response
                app.UseExceptionHandler(errorApp =>
                {
                    errorApp.Run(async context =>
                    {
                        var exceptionHandler = context.Features.Get<IExceptionHandlerFeature>();
                        if (exceptionHandler != null)
                        {
                            logger.LogError(
                                exceptionHandler.Error,
                                "Unhandled exception for {Method} {Path}",
                                context.Request.Method,
                                context.Request.Path
                            );
                        }

                        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                        context.Response.ContentType = "application/json";
                        await context.Response.WriteAsync("{\"error\":\"An unexpected error occurred.\"}");
                    });
                });
            }

            // Add security headers on every response
            var b2cOrigins =
                Configuration.GetSection("B2cCustomPage:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
            app.Use(
                async (context, next) =>
                {
                    if (context.Request.Path.StartsWithSegments("/b2c") && b2cOrigins.Length > 0)
                    {
                        // Azure AD B2C custom pages are loaded cross-origin; use a
                        // CSP frame-ancestors allowlist instead of blanket DENY.
                        context.Response.Headers["Content-Security-Policy"] =
                            "frame-ancestors " + string.Join(" ", b2cOrigins);
                    }
                    else
                    {
                        context.Response.Headers["X-Frame-Options"] = "DENY";
                    }
                    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
                    await next();
                }
            );

            app.UseHttpsRedirection();

            // 301 redirects for legacy Wix routes that crawlers still index.
            MapLegacyWixRedirects(app);

            app.UseResponseCompression();
            app.UseStaticFiles(
                new StaticFileOptions
                {
                    OnPrepareResponse = ctx =>
                    {
                        // Azure AD B2C fetches custom page content via a CORS request from
                        // the b2clogin.com origin. Static files bypass the CORS middleware,
                        // so we add the header here for /b2c/ paths only.
                        if (ctx.Context.Request.Path.StartsWithSegments("/b2c"))
                        {
                            var origin = ctx.Context.Request.Headers["Origin"].ToString();
                            if (b2cOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
                            {
                                ctx.Context.Response.Headers["Access-Control-Allow-Origin"] = origin;
                                ctx.Context.Response.Headers.Append("Vary", "Origin");
                            }
                        }
                    },
                }
            );
            if (!useDevSpaProxy)
            {
                app.UseSpaStaticFiles();
            }
            app.UseRouting();

            // After UseRouting so the selected endpoint is known, and outside everything that
            // follows so it observes the status actually sent - including the 415 that routing
            // itself produces for a wrong content type, which no MVC filter ever sees.
            app.UseMiddleware<UnsubscribeDiagnosticsMiddleware>();

            app.UseCors();
            app.UseAuthentication();
            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapEventDeepLinkOpenGraph(env);
                endpoints.MapBlogDeepLinkOpenGraph(env);
                endpoints.MapHub<EmailJobHub>("/hubs/email-jobs");
                endpoints.MapHub<NotificationsHub>("/hubs/notifications");
                endpoints.MapControllerRoute(name: "default", pattern: "{controller}/{action=Index}/{id?}");
            });

            // Short-circuit requests for paths that are clearly not part of the SPA
            // (e.g. WordPress scanner bots probing /wp-login.php). Without this, they
            // fall through to the SPA default-page middleware which throws an exception.
            app.Use(
                async (context, next) =>
                {
                    var path = context.Request.Path.Value;
                    if (
                        path != null
                        && (
                            path.EndsWith(".php", StringComparison.OrdinalIgnoreCase)
                            || path.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase)
                            || path.EndsWith(".jsp", StringComparison.OrdinalIgnoreCase)
                        )
                    )
                    {
                        context.Response.StatusCode = 404;
                        return;
                    }
                    await next();
                }
            );

            app.UseSpa(spa =>
            {
                // To learn more about options for serving an Angular SPA from ASP.NET Core,
                // see https://go.microsoft.com/fwlink/?linkid=864501

                spa.Options.SourcePath = "ClientApp";

                if (useDevSpaProxy)
                {
                    spa.UseProxyToSpaDevelopmentServer("http://127.0.0.1:4200");
                }
            });
        }

        internal static void MapLegacyWixRedirects(IApplicationBuilder app)
        {
            app.Use(
                async (context, next) =>
                {
                    var path = context.Request.Path.Value;
                    if (
                        path != null
                        && (
                            path.Equals("/faq", StringComparison.OrdinalIgnoreCase)
                            || path.Equals("/about-us", StringComparison.OrdinalIgnoreCase)
                        )
                    )
                    {
                        context.Response.StatusCode = StatusCodes.Status301MovedPermanently;
                        context.Response.Headers.Location = "/about" + context.Request.QueryString;
                        return;
                    }
                    await next();
                }
            );
        }
    }
}
