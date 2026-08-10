using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Web.Configuration;
using Web.Services;
using Xunit;

namespace Web.UnitTests;

public sealed class PostmarkSuppressionDumpClientTests
{
    private const string Token = "unit-test-server-token";

    private static (PostmarkSuppressionDumpClient Client, StubHandler Handler) Create(
        HttpStatusCode status = HttpStatusCode.OK,
        string body = """{"Suppressions":[]}""",
        string serverToken = Token
    )
    {
        var handler = new StubHandler(status, body);
        var client = new PostmarkSuppressionDumpClient(
            new HttpClient(handler, disposeHandler: true),
            Options.Create(new PostmarkOptions { ServerToken = serverToken }),
            NullLogger<PostmarkSuppressionDumpClient>.Instance
        );
        return (client, handler);
    }

    [Fact]
    public async Task Sends_the_server_token_header_to_the_stream_dump_url()
    {
        var (client, handler) = Create();

        await client.GetSuppressionsAsync("broadcast", CancellationToken.None);

        Assert.Equal(HttpMethod.Get, handler.Request.Method);
        Assert.Equal(
            "https://api.postmarkapp.com/message-streams/broadcast/suppressions/dump",
            handler.Request.RequestUri?.ToString()
        );
        Assert.Equal(Token, handler.Request.Headers.GetValues("X-Postmark-Server-Token").Single());
    }

    [Fact]
    public async Task Parses_every_entry_field()
    {
        var body = """
            {
              "Suppressions": [
                {
                  "EmailAddress": "one@example.com",
                  "SuppressionReason": "ManualSuppression",
                  "Origin": "Recipient",
                  "CreatedAt": "2019-12-17T08:58:33-05:00"
                },
                {
                  "EmailAddress": "two@example.com",
                  "SuppressionReason": "HardBounce",
                  "Origin": "Customer",
                  "CreatedAt": "2020-01-01T00:00:00-05:00"
                }
              ]
            }
            """;
        var (client, _) = Create(body: body);

        var entries = await client.GetSuppressionsAsync("outbound", CancellationToken.None);

        Assert.Equal(2, entries.Count);
        Assert.Equal("one@example.com", entries[0].EmailAddress);
        Assert.Equal("ManualSuppression", entries[0].SuppressionReason);
        Assert.Equal("Recipient", entries[0].Origin);
        Assert.Equal("2019-12-17T08:58:33-05:00", entries[0].CreatedAt);
        Assert.Equal("HardBounce", entries[1].SuppressionReason);
    }

    [Fact]
    public async Task Skips_an_entry_with_no_usable_address_without_losing_the_rest()
    {
        var body = """
            {
              "Suppressions": [
                { "SuppressionReason": "ManualSuppression" },
                { "EmailAddress": "   " },
                { "EmailAddress": 42 },
                { "EmailAddress": "real@example.com", "SuppressionReason": "SpamComplaint" }
              ]
            }
            """;
        var (client, _) = Create(body: body);

        var entries = await client.GetSuppressionsAsync("broadcast", CancellationToken.None);

        var entry = Assert.Single(entries);
        Assert.Equal("real@example.com", entry.EmailAddress);
    }

    [Fact]
    public async Task Non_string_optional_fields_read_as_absent()
    {
        // Lenient in the same direction as the webhook handlers: a drifted value is absent, not
        // an exception that loops the whole reconciliation.
        var body = """
            {
              "Suppressions": [
                { "EmailAddress": "real@example.com", "SuppressionReason": 7, "Origin": null, "CreatedAt": false }
              ]
            }
            """;
        var (client, _) = Create(body: body);

        var entries = await client.GetSuppressionsAsync("broadcast", CancellationToken.None);

        var entry = Assert.Single(entries);
        Assert.Null(entry.SuppressionReason);
        Assert.Null(entry.Origin);
        Assert.Null(entry.CreatedAt);
    }

    [Fact]
    public async Task A_dump_without_a_suppressions_array_is_empty()
    {
        var (client, _) = Create(body: """{"Unexpected":true}""");

        var entries = await client.GetSuppressionsAsync("broadcast", CancellationToken.None);

        Assert.Empty(entries);
    }

    [Fact]
    public async Task A_non_success_status_throws()
    {
        var (client, _) = Create(HttpStatusCode.Unauthorized, """{"ErrorCode":401}""");

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetSuppressionsAsync("broadcast", CancellationToken.None)
        );
    }

    [Fact]
    public async Task A_missing_server_token_fails_before_any_request()
    {
        var (client, handler) = Create(serverToken: "");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetSuppressionsAsync("broadcast", CancellationToken.None)
        );
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task A_blank_stream_is_rejected()
    {
        var (client, _) = Create();

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.GetSuppressionsAsync("  ", CancellationToken.None)
        );
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public HttpRequestMessage Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Request = request;
            return Task.FromResult(
                new HttpResponseMessage(_status)
                {
                    Content = new StringContent(_body, Encoding.UTF8, "application/json"),
                }
            );
        }
    }
}
