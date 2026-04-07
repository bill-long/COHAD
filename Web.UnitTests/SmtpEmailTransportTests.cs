using Web.Configuration;
using Web.Services;

namespace Web.UnitTests;

public sealed class SmtpEmailTransportTests
{
    [Fact]
    public void ProviderName_is_SendGrid()
    {
        var transport = new SmtpEmailTransport(
            new SmtpOptions
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

    [Fact]
    public void SmtpOptions_defaults_are_sensible()
    {
        var opts = new SmtpOptions();
        Assert.Equal(30, opts.TimeoutSeconds);
        Assert.Equal(60, opts.MaxIdleSeconds);
    }

    [Fact]
    public void Construction_with_custom_timeout_and_idle_values_succeeds()
    {
        var opts = new SmtpOptions
        {
            SmtpHost = "smtp.example.com",
            SmtpUser = "user",
            SmtpPassword = "pass",
            TimeoutSeconds = 15,
            MaxIdleSeconds = 45,
        };
        using var transport = new SmtpEmailTransport(
            opts,
            logSmtpProtocolOnFailure: true,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SmtpEmailTransport>.Instance
        );
        Assert.Equal("SendGrid", transport.ProviderName);
    }

    [Fact]
    public void Dispose_is_safe_when_never_connected()
    {
        var transport = new SmtpEmailTransport(
            new SmtpOptions { SmtpHost = "localhost", SmtpUser = "u", SmtpPassword = "p" },
            logSmtpProtocolOnFailure: false,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SmtpEmailTransport>.Instance
        );
        // Should not throw even though no connection was ever established
        transport.Dispose();
        transport.Dispose(); // double-dispose safety
    }
}
