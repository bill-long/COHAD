using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Web
{
    public class Program
    {
        public static void Main(string[] args)
        {
            CreateHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                // User secrets are only included by default when ASPNETCORE_ENVIRONMENT is Development.
                // If you run with another environment (or launchSettings omits the variable), load secrets
                // from this assembly so local values like DocumentStorage:ConnectionString still apply.
                .ConfigureAppConfiguration((ctx, config) =>
                {
                    if (!ctx.HostingEnvironment.IsDevelopment())
                    {
                        config.AddUserSecrets(typeof(Program).Assembly, optional: true);
                    }
                })
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup<Startup>();
                });
    }
}
