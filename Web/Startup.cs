using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using CosmosClient = Microsoft.Azure.Cosmos.CosmosClient;
using Web.Authorization;
using Web.Models;
using Web.Repository;
using Web.Services;

namespace Web
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddApplicationInsightsTelemetry();

            services.AddControllers();

            // In production, the Angular files will be served from this directory
            services.AddSpaStaticFiles(configuration =>
            {
                configuration.RootPath = "ClientApp/dist/cohad-app";
            });

            services
                .AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
                })
                .AddJwtBearer(options =>
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

                options.AddPolicy("Resident", policy => policy.Requirements.Add(new RoleAuthorizationRequirement(User.Role.Resident)));
                options.AddPolicy("Administrator", policy => policy.Requirements.Add(new RoleAuthorizationRequirement(User.Role.Administrator)));
                options.AddPolicy("WelcomeCommittee", policy => policy.Requirements.Add(new RoleAuthorizationRequirement(User.Role.WelcomeCommittee)));
                options.AddPolicy("GardenClub", policy => policy.Requirements.Add(new RoleAuthorizationRequirement(User.Role.GardenClub)));
                options.AddPolicy("Board", policy => policy.Requirements.Add(new RoleAuthorizationRequirement(User.Role.Board)));
                options.AddPolicy("SocialCommittee", policy => policy.Requirements.Add(new RoleAuthorizationRequirement(User.Role.SocialCommittee)));
                options.AddPolicy("SunshineCommittee", policy => policy.Requirements.Add(new RoleAuthorizationRequirement(User.Role.SunshineCommittee)));
            });

            services.AddScoped<IAuthorizationHandler, RoleAuthorizationHandler>();

            // Repository stuff
            var uri = Configuration["CosmosUri"];
            var key = Configuration["CosmosKey"];
            var db = Configuration["CosmosDatabase"];

            // services.AddDbContext<CohadWebDbContext>(options => options.UseInMemoryDatabase("CohadWebDebugDatabase"));

            services.AddDbContext<CohadWebDbContext>(options => options.UseCosmos(uri, key, db));
            services.AddSingleton(_ => new CosmosClient(uri, key));
            services.AddSingleton(sp => sp.GetRequiredService<CosmosClient>().GetContainer(db, "Homes"));

            services.AddScoped<IEmailService, EmailService>();
            services.AddScoped<IHomeLookupService, HomeLookupService>();
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            app.UseForwardedHeaders(new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
            });

            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseHsts();
            }

            app.UseHttpsRedirection();
            app.UseStaticFiles();
            app.UseSpaStaticFiles();
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllerRoute(
                    name: "default",
                    pattern: "{controller}/{action=Index}/{id?}");
            });

            app.UseSpa(spa =>
            {
                // To learn more about options for serving an Angular SPA from ASP.NET Core,
                // see https://go.microsoft.com/fwlink/?linkid=864501

                spa.Options.SourcePath = "ClientApp";

                if (env.IsDevelopment())
                {
                    spa.UseProxyToSpaDevelopmentServer("http://localhost:4200");
                }
            });
        }
    }
}
