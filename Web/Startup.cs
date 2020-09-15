using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Web.Authorization;
using Web.Models;
using Web.Repository;

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
            // In production, the Angular files will be served from this directory
            services.AddSpaStaticFiles(configuration =>
            {
                configuration.RootPath = "ClientApp/dist/cohad-app";
            });

            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(options =>
                    {
                        options.Authority = "https://cohad.b2clogin.com/cohad.onmicrosoft.com/v2.0";
                        options.MetadataAddress = "https://cohad.b2clogin.com/cohad.onmicrosoft.com/v2.0/.well-known/openid-configuration?p=b2c_1_v2_signup_signin";
                        options.Audience = "6034a3a8-53b5-401b-a66f-54be5966a067";
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
                options.AddPolicy("Member", policy => policy.Requirements.Add(new RoleAuthorizationRequirement(User.Roles.Member)));
                options.AddPolicy("Admin", policy => policy.Requirements.Add(new RoleAuthorizationRequirement(User.Roles.Administrator)));
            });

            services.AddScoped<IAuthorizationHandler, RoleAuthorizationHandler>();

            // Repository stuff
            var uri = Configuration["CosmosUri"];
            var key = Configuration["CosmosKey"];
            var db = Configuration["CosmosDatabase"];

#if DEBUG
            services.AddDbContext<CohadWebDbContext>(options => options.UseInMemoryDatabase("CohadWebDebugDatabase"));
#else
            services.AddDbContext<CohadWebDbContext>(options => options.UseCosmos(uri, key, db));
#endif
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
            app.UseAuthentication();
            app.UseAuthorization();

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
