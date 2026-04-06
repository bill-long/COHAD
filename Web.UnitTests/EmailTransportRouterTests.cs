using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Options;
using Moq;
using Web.Configuration;
using Web.Services;

namespace Web.UnitTests;

public sealed class EmailTransportRouterTests
{
    private readonly Mock<IEmailTransport> _smtpTransport = new();
    private readonly Mock<IEmailTransport> _sesTransport = new();

    public EmailTransportRouterTests()
    {
        _smtpTransport.Setup(t => t.ProviderName).Returns("SendGrid");
        _sesTransport.Setup(t => t.ProviderName).Returns("Ses");
    }

    private EmailTransportRouter CreateRouter(bool sesEnabled = false, List<string>? routedRecipients = null)
    {
        var opts = Options.Create(
            new SesOptions { Enabled = sesEnabled, RoutedRecipients = routedRecipients ?? new List<string>() }
        );
        return new EmailTransportRouter(_smtpTransport.Object, _sesTransport.Object, opts);
    }

    [Fact]
    public void Returns_smtp_when_ses_disabled()
    {
        var router = CreateRouter(sesEnabled: false, routedRecipients: new List<string> { "ses@test.com" });
        var transport = router.GetTransportForRecipient("ses@test.com");
        Assert.Equal("SendGrid", transport.ProviderName);
    }

    [Fact]
    public void Returns_smtp_for_non_routed_recipient()
    {
        var router = CreateRouter(sesEnabled: true, routedRecipients: new List<string> { "ses@test.com" });
        var transport = router.GetTransportForRecipient("other@test.com");
        Assert.Equal("SendGrid", transport.ProviderName);
    }

    [Fact]
    public void Returns_ses_for_routed_recipient()
    {
        var router = CreateRouter(sesEnabled: true, routedRecipients: new List<string> { "ses@test.com" });
        var transport = router.GetTransportForRecipient("ses@test.com");
        Assert.Equal("Ses", transport.ProviderName);
    }

    [Fact]
    public void Routing_is_case_insensitive()
    {
        var router = CreateRouter(sesEnabled: true, routedRecipients: new List<string> { "SES@Test.COM" });
        var transport = router.GetTransportForRecipient("ses@test.com");
        Assert.Equal("Ses", transport.ProviderName);
    }

    [Fact]
    public void Returns_smtp_when_routed_list_empty()
    {
        var router = CreateRouter(sesEnabled: true, routedRecipients: new List<string>());
        var transport = router.GetTransportForRecipient("any@test.com");
        Assert.Equal("SendGrid", transport.ProviderName);
    }
}
