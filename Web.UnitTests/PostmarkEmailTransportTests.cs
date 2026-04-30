using Web.Configuration;
using Web.Services;
using Xunit;

namespace Web.UnitTests;

public sealed class PostmarkEmailTransportTests
{
    [Fact]
    public void ProviderName_Is_Postmark()
    {
        var opts = new PostmarkOptions { ServerToken = "test-token" };
        using var transport = new PostmarkEmailTransport(
            opts,
            logSmtpProtocolOnFailure: false,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PostmarkEmailTransport>.Instance
        );
        Assert.Equal("Postmark", transport.ProviderName);
    }

    [Fact]
    public void Dispose_Is_Safe_When_Never_Connected()
    {
        var opts = new PostmarkOptions { ServerToken = "test-token" };
        var transport = new PostmarkEmailTransport(
            opts,
            logSmtpProtocolOnFailure: false,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PostmarkEmailTransport>.Instance
        );
        transport.Dispose();
        transport.Dispose(); // double-dispose safety
    }
}
