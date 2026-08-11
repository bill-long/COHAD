using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Web.Configuration;
using Web.Services;
using Xunit;

namespace Web.UnitTests;

public sealed class PostmarkReactivationServiceTests
{
    private static PostmarkReactivationService Create(
        IPostmarkSuppressionClient client,
        string broadcastStream = "broadcast",
        string transactionalStream = "outbound"
    )
    {
        return new PostmarkReactivationService(
            client,
            Options.Create(
                new PostmarkOptions
                {
                    BroadcastStream = broadcastStream,
                    TransactionalStream = transactionalStream,
                }
            ),
            NullLogger<PostmarkReactivationService>.Instance
        );
    }

    [Fact]
    public async Task Targets_every_configured_stream()
    {
        // The COHAD record does not store which stream suppressed the address, and deleting an
        // absent entry is a provider-side no-op - so both streams are always targeted.
        var client = new Mock<IPostmarkSuppressionClient>();
        var service = Create(client.Object);

        var result = await service.ReactivateAsync("jane@example.com", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.StreamsAttempted);
        client.Verify(c => c.ReactivateAsync("broadcast", "jane@example.com", It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(c => c.ReactivateAsync("outbound", "jane@example.com", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Identical_stream_settings_are_deduped()
    {
        var client = new Mock<IPostmarkSuppressionClient>();
        var service = Create(client.Object, broadcastStream: "broadcast", transactionalStream: "broadcast");

        var result = await service.ReactivateAsync("jane@example.com", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.StreamsAttempted);
        client.Verify(c => c.ReactivateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_failed_stream_is_reported_and_does_not_stop_the_other()
    {
        var client = new Mock<IPostmarkSuppressionClient>();
        client
            .Setup(c => c.ReactivateAsync("broadcast", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("provider said no"));
        var service = Create(client.Object);

        var result = await service.ReactivateAsync("jane@example.com", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(new[] { "broadcast" }, result.FailedStreams);
        client.Verify(c => c.ReactivateAsync("outbound", "jane@example.com", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task No_configured_streams_is_not_a_success()
    {
        // The provider side was not touched, which is exactly what the caller's warning exists
        // to say.
        var client = new Mock<IPostmarkSuppressionClient>();
        var service = Create(client.Object, broadcastStream: "", transactionalStream: " ");

        var result = await service.ReactivateAsync("jane@example.com", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(0, result.StreamsAttempted);
        client.Verify(
            c => c.ReactivateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    [Fact]
    public async Task Cancellation_propagates_rather_than_reading_as_a_failed_stream()
    {
        var client = new Mock<IPostmarkSuppressionClient>();
        client
            .Setup(c => c.ReactivateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        var service = Create(client.Object);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.ReactivateAsync("jane@example.com", CancellationToken.None)
        );
    }

    // --- MockPostmarkSuppressionClient parity (the MockData stand-in must close the same loop
    // the real provider closes: a reactivated address stops appearing in the dump) ---

    [Fact]
    public async Task Mock_client_reactivation_removes_the_dump_entry_case_insensitively()
    {
        var mock = new Web.MockData.MockPostmarkSuppressionClient().SeedSampleData();

        await mock.ReactivateAsync("broadcast", "POSTMARK.UNSUBSCRIBED@COHAD.LOCAL", CancellationToken.None);

        Assert.Empty(await mock.GetSuppressionsAsync("broadcast", CancellationToken.None));
    }

    [Fact]
    public async Task Mock_client_reactivation_of_an_absent_entry_is_a_noop_success()
    {
        var mock = new Web.MockData.MockPostmarkSuppressionClient().SeedSampleData();

        await mock.ReactivateAsync("broadcast", "someone.else@cohad.local", CancellationToken.None);
        await mock.ReactivateAsync("outbound", "postmark.unsubscribed@cohad.local", CancellationToken.None);

        var remaining = await mock.GetSuppressionsAsync("broadcast", CancellationToken.None);
        Assert.Single(remaining);
    }
}
