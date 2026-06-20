using System;
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

                // Any role that can send committee emails — used for email job management endpoints and the SignalR hub.
                options.AddPolicy(
                    "EmailSender",
                    policy =>
                        policy.Requirements.Add(
                            new AnyRoleAuthorizationRequirement(
                                User.Role.Administrator,
                                User.Role.Board,
                                User.Role.WelcomeCommittee,
                                User.Role.GardenClub,
                                User.Role.SocialCommittee,
                                User.Role.SunshineCommittee
                            )
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
                            new AnyRoleAuthorizationRequirement(
                                User.Role.Administrator,
                                User.Role.Board,
                                User.Role.WelcomeCommittee,
                                User.Role.GardenClub,
                                User.Role.SocialCommittee,
                                User.Role.SunshineCommittee,
                                User.Role.ArchitecturalCommittee,
                                User.Role.LandscapeCommittee
                            )
                        )
                );
            });

            services.AddScoped<IAuthorizationHandler, RoleAuthorizationHandler>();
            services.AddScoped<IAuthorizationHandler, AnyRoleAuthorizationHandler>();
            services.AddSingleton<IOgThumbnailService, SkiaSharpOgThumbnailService>();
            services.AddSingleton<IImageConversionService, SkiaSharpImageConversionService>();
            services.AddSingleton<IImageUploadHelper, ImageUploadHelper>();
            services.AddScoped<IEventSignupConversionService, EventSignupConversionService>();

            // Unsubscribe token service — only registered when a signing key is configured.
            // Without a key, the UnsubscribeController still works (returns 400 for all tokens)
            // and EmailJobProcessor sends emails without unsubscribe headers/footer.
            var unsubKey = Configuration.GetSection("UnsubscribeToken")["SigningKey"];
            if (!string.IsNullOrWhiteSpace(unsubKey) && Encoding.UTF8.GetByteCount(unsubKey) >= 32)
            {
                services.AddSingleton<IUnsubscribeTokenService, UnsubscribeTokenService>();
            }
            else
            {
                services.AddSingleton<IUnsubscribeTokenService, NullUnsubscribeTokenService>();
            }

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
                services.AddSingleton<IHeldMessageRepository, MockHeldMessageRepository>();
                services.AddSingleton<INotificationRepository, MockNotificationRepository>();
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

            // Notification service (shared across environments). The SignalR-backed realtime notifier
            // is wired in a later phase; until then a no-op notifier is used and clients pick up
            // changes on their next fetch.
            services.AddSingleton<INotificationRealtimeNotifier, NoOpNotificationRealtimeNotifier>();
            services.AddScoped<INotificationService, NotificationService>();

            // Email job queue and background processor (shared across environments)
            services.AddSingleton<EmailJobQueue>();
            services.AddSingleton<EmailJobProcessor>();
            services.AddHostedService(sp => sp.GetRequiredService<EmailJobProcessor>());

            // Committee mail poller — polls shared mailboxes and creates forwarding EmailJobs.
            // Only registered when Graph API credentials are configured; CommitteeForwarding:Enabled
            // must also be true for the poller to actually run.
            if (services.Any(sd => sd.ServiceType == typeof(IGraphMailReader)))
            {
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
            app.UseForwardedHeaders(
                new ForwardedHeadersOptions
                {
                    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
                }
            );

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
            app.UseCors();
            app.UseAuthentication();
            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapEventDeepLinkOpenGraph(env);
                endpoints.MapBlogDeepLinkOpenGraph(env);
                endpoints.MapHub<VendorFlagNotificationsHub>("/hubs/vendor-flags");
                endpoints.MapHub<HeldMessageNotificationsHub>("/hubs/held-messages");
                endpoints.MapHub<EmailJobHub>("/hubs/email-jobs");
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
