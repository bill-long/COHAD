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
                // For the local-only MockData environment, also load user secrets so values like
                // DocumentStorage:ConnectionString apply without loading secrets in staging/production.
                .ConfigureAppConfiguration((ctx, config) =>
                {
                    if (ctx.HostingEnvironment.IsEnvironment("MockData"))
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
