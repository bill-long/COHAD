using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Amazon.SimpleEmailV2;
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
                                User.Role.ArchitecturalCommittee
                            )
                        )
                );
            });

            services.AddScoped<IAuthorizationHandler, RoleAuthorizationHandler>();
            services.AddScoped<IAuthorizationHandler, AnyRoleAuthorizationHandler>();
            services.AddSingleton<IOgThumbnailService, SkiaSharpOgThumbnailService>();
            services.AddSingleton<IImageConversionService, SkiaSharpImageConversionService>();
            services.AddSingleton<IImageUploadHelper, ImageUploadHelper>();

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
                services.AddSingleton<IGraphMailboxService, MockGraphMailboxService>();
                services.AddSingleton<IDocumentFileStore>(sp => new CachedDocumentFileStore(
                    new MockDocumentFileStore()
                ));
                // Seeds sample completed jobs + HTML blobs for Manage → Email testing.
                services.AddSingleton<IEmailJobRepository>(sp => new MockEmailJobRepository(
                    sp.GetRequiredService<IDocumentFileStore>()
                ));
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

                // Graph API for committee mailbox forwarding — registered only when credentials are configured.
                var graphTenantId = Configuration["Graph:TenantId"];
                var graphClientId = Configuration["Graph:ClientId"];
                var graphClientSecret = Configuration["Graph:ClientSecret"];
                if (
                    !string.IsNullOrWhiteSpace(graphTenantId)
                    && !string.IsNullOrWhiteSpace(graphClientId)
                    && !string.IsNullOrWhiteSpace(graphClientSecret)
                )
                {
                    services.AddSingleton<IGraphMailboxService, GraphMailboxService>();
                }
                else
                {
                    services.AddSingleton<IGraphMailboxService, NotConfiguredGraphMailboxService>();
                }
            }

            // Email job queue and background processor (shared across environments)
            services.AddSingleton<EmailJobQueue>();
            services.AddSingleton<EmailJobProcessor>();
            services.AddHostedService(sp => sp.GetRequiredService<EmailJobProcessor>());

            // Email transport abstraction (SMTP / SES per-recipient routing)
            services.Configure<SesOptions>(Configuration.GetSection("Ses"));
            if (useMockData)
            {
                // MockData environment uses MockEmailTransport for everything — force SES disabled
                // via PostConfigure so IOptions<SesOptions> consumers see a single source of truth.
                services.PostConfigure<SesOptions>(opts => opts.Enabled = false);
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

            var sesOptions = Configuration.GetSection("Ses").Get<SesOptions>() ?? new SesOptions();
            if (useMockData)
                sesOptions.Enabled = false;
            if (sesOptions.Enabled)
            {
                if (string.IsNullOrWhiteSpace(sesOptions.Region))
                {
                    throw new InvalidOperationException(
                        "SES is enabled, but Ses:Region is missing or empty. "
                            + "Configure a valid AWS region system name such as 'us-west-2'."
                    );
                }

                var sesRegion = Amazon.RegionEndpoint.EnumerableAllRegions.FirstOrDefault(r =>
                    string.Equals(r.SystemName, sesOptions.Region, StringComparison.OrdinalIgnoreCase)
                );

                if (sesRegion == null)
                {
                    throw new InvalidOperationException(
                        $"SES is enabled, but Ses:Region '{sesOptions.Region}' is not a valid AWS region system name."
                    );
                }

                services.AddSingleton<IAmazonSimpleEmailServiceV2>(sp =>
                {
                    return new Amazon.SimpleEmailV2.AmazonSimpleEmailServiceV2Client(sesRegion);
                });
                services.AddSingleton<SesEmailTransport>();
            }

            services.AddSingleton<EmailTransportRouter>(sp =>
            {
                var smtp = sp.GetRequiredService<IEmailTransport>();
                // When SES is disabled, use SMTP as the SES transport fallback (router always returns SMTP)
                var ses = sesOptions.Enabled ? (IEmailTransport)sp.GetRequiredService<SesEmailTransport>() : smtp;
                return new EmailTransportRouter(smtp, ses, sp.GetRequiredService<IOptions<SesOptions>>());
            });

            // Retention cleanup (invoked on submission; best-effort)
            services.AddScoped<EmailJobCleanupService>();

            // Webhook verification and delivery tracking
            services.Configure<SendGridOptions>(Configuration.GetSection("SendGrid"));
            services.AddSingleton<ISendGridWebhookVerifier, SendGridWebhookVerifier>();
            services.AddScoped<IEmailDeliveryActionService, EmailDeliveryActionService>();

            // HttpClientFactory for SNS signature certificate download
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
    }
}
