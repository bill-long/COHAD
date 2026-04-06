using Web.Services;

namespace Web.UnitTests;

public sealed class SmtpEmailTransportTests
{
    [Fact]
    public void ProviderName_is_SendGrid()
    {
        var transport = new SmtpEmailTransport(
            new Web.Configuration.SmtpOptions
            {
                SmtpHost = "localhost",
                SmtpUser = "u",
                SmtpPassword = "p",
            },
            logSmtpProtocolOnFailure: false,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SmtpEmailTransport>.Instance
        );
        Assert.Equal("SendGrid", transport.ProviderName);
        transport.Dispose();
    }
}
