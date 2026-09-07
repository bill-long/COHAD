using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Newtonsoft.Json.Linq;
using Web.Configuration;
using Web.Controllers;
using Web.Services.Repositories;
using Xunit;

namespace Web.UnitTests;

public class CosmosNotFoundPipelineTests
{
    [Theory]
    [InlineData(0, HttpStatusCode.NotFound)]
    [InlineData(1003, HttpStatusCode.InternalServerError)]
    public async Task Committee_photo_distinguishes_missing_item_from_storage_failure(
        int subStatus,
        HttpStatusCode expected
    )
    {
        var container = new Mock<Container>();
        container
            .Setup(c =>
                c.ReadItemAsync<JObject>(
                    It.IsAny<string>(),
                    It.IsAny<PartitionKey>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ThrowsAsync(new CosmosException("Missing", HttpStatusCode.NotFound, subStatus, "activity", 0));
        // Exercise the real public photo endpoint and Cosmos repository; only the storage transport is mocked.
        using var host = await new HostBuilder()
            .ConfigureWebHost(web =>
                web.UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddLogging(b => b.ClearProviders());
                        services.AddAuthorization();
                        services
                            .AddControllers()
                            .AddApplicationPart(typeof(CommitteeController).Assembly)
                            .AddControllersAsServices();
                        services.AddTransient(_ => new CommitteeController(
                            new CosmosCommitteeRepository(container.Object),
                            null,
                            null,
                            null,
                            null,
                            null,
                            null,
                            null,
                            null,
                            null,
                            Options.Create(new DocumentStorageOptions()),
                            null,
                            null,
                            null
                        ));
                    })
                    .Configure(app =>
                    {
                        app.UseExceptionHandler(handler =>
                            handler.Run(context =>
                            {
                                context.Response.StatusCode = 500;
                                return Task.CompletedTask;
                            })
                        );
                        app.UseRouting();
                        app.UseAuthorization();
                        app.UseEndpoints(endpoints => endpoints.MapControllers());
                    })
            )
            .StartAsync();
        using var client = host.GetTestClient();

        var response = await client.GetAsync(
            "/api/committee/social/members/11111111-1111-1111-1111-111111111111/photo"
        );

        Assert.Equal(expected, response.StatusCode);
        container.VerifyAll();
    }
}
