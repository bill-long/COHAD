using System;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using CosmosClient = Microsoft.Azure.Cosmos.CosmosClient;
using Web.Authorization;
using Web.Configuration;
using Web.MockData;
using Web.Models;
using Web.Services;
using Web.Services.Repositories;

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
            services.AddApplicationInsightsTelemetry();

            services.AddControllers();
            services.AddMemoryCache();

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
                    policy.WithOrigins(Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? Array.Empty<string>())
                          .AllowAnyHeader()
                          .AllowAnyMethod();
                });
            });

            var useMockData = Environment.IsEnvironment("MockData");
            services.Configure<DocumentStorageOptions>(Configuration.GetSection("DocumentStorage"));
            // Keep multipart request cap aligned with DocumentStorage:MaxUploadBytes (DocumentController enforces the same limit).
            services.Configure<FormOptions>(options =>
            {
                var max = Configuration.GetSection("DocumentStorage").GetValue<long?>("MaxUploadBytes")
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
                            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey))
                        };
                    }
                    else
                    {
                        options.TokenValidationParameters = new TokenValidationParameters
                        {
                            ValidateAudience = true,
                            ValidateIssuer = true,
                            ValidIssuer = "https://cohadorgb2c.b2clogin.com/a7e9006b-c606-4670-960c-3998b35ea5ee/v2.0/",
                            ValidAudience = "5803d9fa-a62f-401c-b0f4-269b3cb468eb"
                        };

                        options.MetadataAddress =
                            "https://cohadorgb2c.b2clogin.com/cohadorgb2c.onmicrosoft.com/b2c_1_default/v2.0/.well-known/openid-configuration";
                    }
                });

            // Allow reverse proxy from nginx
            services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders =
                    ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            });

            // Authorization stuff - make sure users have required roles
            services.AddAuthorization(options =>
            {
                options.DefaultPolicy = new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                    .RequireClaim("http://schemas.microsoft.com/identity/claims/scope", "API")
                    .Build();

                // Role hierarchy: every Administrator is also assigned the Resident role.
                // Controllers using [Authorize(Policy = "Resident")] therefore implicitly permit Administrators.
                // Do not add a separate "Resident OR Administrator" check — it is unnecessary.
                options.AddPolicy("Resident", policy => policy.Requirements.Add(new RoleAuthorizationRequirement(User.Role.Resident)));
                options.AddPolicy("Administrator", policy => policy.Requirements.Add(new RoleAuthorizationRequirement(User.Role.Administrator)));
                options.AddPolicy("WelcomeCommittee", policy => policy.Requirements.Add(new RoleAuthorizationRequirement(User.Role.WelcomeCommittee)));
                options.AddPolicy("GardenClub", policy => policy.Requirements.Add(new RoleAuthorizationRequirement(User.Role.GardenClub)));
                options.AddPolicy("Board", policy => policy.Requirements.Add(new RoleAuthorizationRequirement(User.Role.Board)));
                options.AddPolicy("SocialCommittee", policy => policy.Requirements.Add(new RoleAuthorizationRequirement(User.Role.SocialCommittee)));
                options.AddPolicy("SunshineCommittee", policy => policy.Requirements.Add(new RoleAuthorizationRequirement(User.Role.SunshineCommittee)));
            });

            services.AddScoped<IAuthorizationHandler, RoleAuthorizationHandler>();

            // Repository / persistence
            if (useMockData)
            {
                services.AddSingleton<IUserRepository, MockUserRepository>();
                services.AddSingleton<IHomeRepository, MockHomeRepository>();
                services.AddSingleton<IPaymentRepository>(sp =>
                    new MockPaymentRepository(
                        sp.GetRequiredService<IHomeRepository>(),
                        sp.GetRequiredService<IUserRepository>()));
                services.AddSingleton<IAuditLogRepository, MockAuditLogRepository>();
                services.AddSingleton<IDocumentRepository, MockDocumentRepository>();
                services.AddSingleton<ICommunityEventRepository, MockCommunityEventRepository>();
                services.AddSingleton<IVendorRepository, MockVendorRepository>();
                services.AddSingleton<IVendorReviewRepository, MockVendorReviewRepository>();
                services.AddSingleton<IYouthServiceListingRepository, MockYouthServiceListingRepository>();
                services.AddSingleton<IDocumentFileStore, MockDocumentFileStore>();
                services.AddScoped<IEmailService, NoOpEmailService>();
            }
            else
            {
                var uri = Configuration["CosmosUri"];
                var key = Configuration["CosmosKey"];
                var db = Configuration["CosmosDatabase"];

                services.AddSingleton(_ => new CosmosClient(uri, key));
                services.AddScoped<IUserRepository>(sp =>
                    new CosmosUserRepository(sp.GetRequiredService<CosmosClient>().GetContainer(db, "Users")));
                services.AddScoped<IHomeRepository>(sp =>
                    new CosmosHomeRepository(sp.GetRequiredService<CosmosClient>().GetContainer(db, "Homes")));
                services.AddScoped<IPaymentRepository>(sp =>
                    new CosmosPaymentRepository(
                        sp.GetRequiredService<CosmosClient>().GetContainer(db, "Payments"),
                        sp.GetRequiredService<IHomeRepository>(),
                        sp.GetRequiredService<IUserRepository>()));
                services.AddScoped<IAuditLogRepository>(sp =>
                    new CosmosAuditLogRepository(
                        sp.GetRequiredService<CosmosClient>().GetContainer(db, "AuditLog"),
                        sp.GetRequiredService<ILogger<CosmosAuditLogRepository>>()));
                services.AddScoped<IDocumentRepository>(sp =>
                    new CosmosDocumentRepository(sp.GetRequiredService<CosmosClient>().GetContainer(db, "Documents")));
                services.AddScoped<ICommunityEventRepository>(sp =>
                    new CosmosCommunityEventRepository(sp.GetRequiredService<CosmosClient>().GetContainer(db, "Events")));
                services.AddScoped<IVendorRepository>(sp =>
                    new CosmosVendorRepository(sp.GetRequiredService<CosmosClient>().GetContainer(db, "Vendors")));
                services.AddScoped<IVendorReviewRepository>(sp =>
                    new CosmosVendorReviewRepository(sp.GetRequiredService<CosmosClient>().GetContainer(db, "VendorReviews")));
                services.AddScoped<IYouthServiceListingRepository>(sp =>
                    new CosmosYouthServiceListingRepository(sp.GetRequiredService<CosmosClient>().GetContainer(db, "YouthServices")));
                services.AddSingleton<IDocumentFileStore, AzureBlobDocumentFileStore>();

                services.AddScoped<IEmailService, EmailService>();
            }
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, ILogger<Startup> logger)
        {
            app.UseForwardedHeaders(new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
            });

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
                            logger.LogError(exceptionHandler.Error, "Unhandled exception for {Method} {Path}", context.Request.Method, context.Request.Path);
                        }

                        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                        context.Response.ContentType = "application/json";
                        await context.Response.WriteAsync("{\"error\":\"An unexpected error occurred.\"}");
                    });
                });
            }

            // Add security headers on every response
            app.Use(async (context, next) =>
            {
                context.Response.Headers["X-Frame-Options"] = "DENY";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
                await next();
            });

            app.UseHttpsRedirection();
            app.UseStaticFiles();
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
                endpoints.MapControllerRoute(
                    name: "default",
                    pattern: "{controller}/{action=Index}/{id?}");
            });

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
