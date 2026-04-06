using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Web.MockData;
using Web.Models;
using Web.Services;
using Web.Services.Repositories;
using Xunit;

namespace Web.UnitTests;

public sealed class PayPalPaymentSyncRunnerTests
{
    [Fact]
    public async Task RunAsync_links_existing_payment_without_home_when_email_matches_directory()
    {
        var homes = new MockHomeRepository();
        var residents = new MockResidentRepository();
        var users = new MockUserRepository();
        var payments = new MockPaymentRepository(homes, residents, users);

        var paymentId = Guid.NewGuid();
        await payments.AddAsync(
            new Payment
            {
                Id = paymentId,
                PayerEmail = "mock@cohad.local",
                PayerUniqueId = null,
                HomeId = null,
                Amount = "50.00",
                PaymentType = Payment.Type.PayPalSync,
                PayPalTransactionId = "TX-EXISTING-UNLINKED",
            }
        );

        var runner = CreateRunner(payments, users, homes, residents, syncLookbackDays: 1);
        var result = await runner.RunAsync();

        Assert.Equal(0, result.Inserted);
        Assert.Equal(1, result.ExistingLinked);

        var onHome = await payments.GetByHomeIdAsync(MockDataConstants.SampleHomeId);
        var updated = Assert.Single(onHome);
        Assert.Equal(paymentId, updated.Id);
        Assert.Equal(MockDataConstants.SampleHomeId, updated.HomeId);
        Assert.Equal(MockDataConstants.AdminUniqueId, updated.PayerUniqueId);
    }

    [Fact]
    public async Task RunAsync_does_not_count_existing_linked_when_email_has_no_home_match()
    {
        var homes = new MockHomeRepository();
        var residents = new MockResidentRepository();
        var users = new MockUserRepository();
        var payments = new MockPaymentRepository(homes, residents, users);

        await payments.AddAsync(
            new Payment
            {
                Id = Guid.NewGuid(),
                PayerEmail = "nobody@example.com",
                PayerUniqueId = null,
                HomeId = null,
                Amount = "10.00",
                PaymentType = Payment.Type.PayPalSync,
                PayPalTransactionId = "TX-ORPHAN",
            }
        );

        var runner = CreateRunner(payments, users, homes, residents, syncLookbackDays: 1);
        var result = await runner.RunAsync();

        Assert.Equal(0, result.ExistingLinked);
    }

    [Fact]
    public async Task ListAllTransactions_treats_404_InvalidRequest_as_empty()
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

        var results = await client.ListAllTransactionDetailsAsync(
            DateTime.UtcNow.AddDays(-1),
            DateTime.UtcNow,
            CancellationToken.None
        );

        Assert.Empty(results);
    }

    private static PayPalPaymentSyncRunner CreateRunner(
        MockPaymentRepository payments,
        MockUserRepository users,
        MockHomeRepository homes,
        MockResidentRepository residents,
        int syncLookbackDays
    )
    {
        var handler = new PayPalStubHandler();
        var http = new HttpClient(handler, disposeHandler: true);

        var payPalOptions = Options.Create(
            new PayPalOptions
            {
                ClientId = "unit-test-client",
                ClientSecret = "unit-test-secret",
                ApiBaseUrl = "https://api-m.paypal.com",
                SyncLookbackDays = syncLookbackDays,
            }
        );

        var payPalClient = new PayPalTransactionSearchClient(
            http,
            payPalOptions,
            NullLogger<PayPalTransactionSearchClient>.Instance
        );

        return new PayPalPaymentSyncRunner(
            payPalClient,
            payments,
            users,
            homes,
            residents,
            payPalOptions,
            NullLogger<PayPalPaymentSyncRunner>.Instance
        );
    }

    /// <summary>Returns OAuth token and empty Transaction Search pages (no new inserts).
    /// </summary>
    private sealed class PayPalStubHandler : HttpMessageHandler
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
                var body = "{\"total_pages\":1,\"transaction_details\":[]}";
                return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json"),
                    }
                );
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
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
