using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.WindowsAzure.Storage.Table;
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
            services.AddMvc().SetCompatibilityVersion(CompatibilityVersion.Version_2_1);

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
                        options.Audience = "f172253e-dc5f-4429-818f-fc506f6bf4a6";
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

            services.AddSingleton<IAuthorizationHandler, RoleAuthorizationHandler>();

            // Repository stuff
            var storageAccount = CloudStorageAccount.Parse(Configuration["CohadConnectionString"]);
            var tableClient = new CloudTableClient(storageAccount.TableStorageUri, storageAccount.Credentials);
            services.AddSingleton<AzureTableRepository<User>>(sp => new AzureTableRepository<User>(tableClient.GetTableReference("Users")));

            // Document storage
            var blobClient = new CloudBlobClient(storageAccount.BlobStorageUri, storageAccount.Credentials);
            services.AddSingleton(sp => new DocumentService(blobClient.GetContainerReference("shared-documents")));
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IHostingEnvironment env)
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
            app.UseMvc();

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
