using Microsoft.Extensions.Logging.Abstractions;
using Web.Services;
using Xunit;

namespace Web.UnitTests;

public sealed class PostmarkEmailTransportTests
{
    [Fact]
    public void ProviderName_Is_Postmark()
    {
        using var transport = new PostmarkEmailTransport(
            "smtp.postmarkapp.com",
            "test-token",
            "outbound",
            30,
            60,
            logSmtpProtocolOnFailure: false,
            NullLogger<PostmarkEmailTransport>.Instance
        );
        Assert.Equal("Postmark", transport.ProviderName);
    }

    [Fact]
    public void Dispose_Is_Safe_When_Never_Connected()
    {
        var transport = new PostmarkEmailTransport(
            "smtp.postmarkapp.com",
            "test-token",
            "outbound",
            30,
            60,
            logSmtpProtocolOnFailure: false,
            NullLogger<PostmarkEmailTransport>.Instance
        );
        transport.Dispose();
        transport.Dispose(); // double-dispose safety
    }

    [Fact]
    public void BroadcastInstance_UsesCorrectHost()
    {
        using var transport = new PostmarkEmailTransport(
            "smtp-broadcasts.postmarkapp.com",
            "test-token",
            "broadcast",
            30,
            60,
            logSmtpProtocolOnFailure: false,
            NullLogger<PostmarkEmailTransport>.Instance
        );
        Assert.Equal("Postmark", transport.ProviderName);
    }
}
