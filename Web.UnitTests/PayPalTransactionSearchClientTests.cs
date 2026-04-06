using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Web.Services;
using Xunit;

namespace Web.UnitTests;

public sealed class PayPalTransactionSearchClientTests
{
    [Fact]
    public async Task ListAllTransactionDetailsAsync_treats_404_InvalidRequest_as_empty()
    {
        var handler = new PayPal404StubHandler();
        var http = new HttpClient(handler, disposeHandler: true);
        var options = Options.Create(
            new PayPalOptions
            {
                ClientId = "unit-test-client",
                ClientSecret = "unit-test-secret",
                ApiBaseUrl = "https://api-m.paypal.com",
                SyncLookbackDays = 1,
            }
        );
        var client = new PayPalTransactionSearchClient(
            http,
            options,
            NullLogger<PayPalTransactionSearchClient>.Instance
        );

        var now = DateTime.UtcNow;
        var results = await client.ListAllTransactionDetailsAsync(now.AddDays(-1), now, CancellationToken.None);

        Assert.Empty(results);
    }

    /// <summary>Returns OAuth token but 404 INVALID_REQUEST for transaction queries.</summary>
    private sealed class PayPal404StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var path = request.RequestUri?.AbsolutePath ?? "";

            if (path.Contains("oauth2/token", StringComparison.OrdinalIgnoreCase))
            {
                var body = "{\"access_token\":\"stub\",\"token_type\":\"Bearer\",\"expires_in\":3600}";
                return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json"),
                    }
                );
            }

            if (path.Contains("reporting/transactions", StringComparison.OrdinalIgnoreCase))
            {
                var body =
                    "{\"name\":\"INVALID_REQUEST\",\"message\":\"Data for the given start date is not available.\",\"debug_id\":\"test\",\"details\":[],\"links\":[]}";
                return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.NotFound)
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json"),
                    }
                );
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
