using System.Collections.Generic;
using Microsoft.Extensions.Options;
using Moq;
using Web.Configuration;
using Web.Services;
using Xunit;

namespace Web.UnitTests;

public sealed class EmailTransportRouterTests
{
    private static IEmailTransport CreateMockTransport(string providerName)
    {
        var mock = new Mock<IEmailTransport>();
        mock.Setup(t => t.ProviderName).Returns(providerName);
        return mock.Object;
    }

    [Fact]
    public void WhenDisabled_AlwaysReturnsDefault()
    {
        var defaultTransport = CreateMockTransport("SendGrid");
        var postmarkTransport = CreateMockTransport("Postmark");
        var options = Options.Create(new PostmarkOptions
        {
            Enabled = false,
            RoutedRecipients = new List<string> { "test@example.com" },
        });

        var router = new EmailTransportRouter(defaultTransport, postmarkTransport, options);

        Assert.Equal("SendGrid", router.GetTransportForRecipient("test@example.com").ProviderName);
        Assert.Equal("SendGrid", router.GetTransportForRecipient("other@example.com").ProviderName);
    }

    [Fact]
    public void WhenEnabled_RoutesListedRecipients()
    {
        var defaultTransport = CreateMockTransport("SendGrid");
        var postmarkTransport = CreateMockTransport("Postmark");
        var options = Options.Create(new PostmarkOptions
        {
            Enabled = true,
            RoutedRecipients = new List<string> { "routed@example.com" },
        });

        var router = new EmailTransportRouter(defaultTransport, postmarkTransport, options);

        Assert.Equal("Postmark", router.GetTransportForRecipient("routed@example.com").ProviderName);
        Assert.Equal("SendGrid", router.GetTransportForRecipient("other@example.com").ProviderName);
    }

    [Fact]
    public void Routing_IsCaseInsensitive()
    {
        var defaultTransport = CreateMockTransport("SendGrid");
        var postmarkTransport = CreateMockTransport("Postmark");
        var options = Options.Create(new PostmarkOptions
        {
            Enabled = true,
            RoutedRecipients = new List<string> { "User@Example.COM" },
        });

        var router = new EmailTransportRouter(defaultTransport, postmarkTransport, options);

        Assert.Equal("Postmark", router.GetTransportForRecipient("user@example.com").ProviderName);
        Assert.Equal("Postmark", router.GetTransportForRecipient("USER@EXAMPLE.COM").ProviderName);
    }

    [Fact]
    public void Routing_TrimsWhitespace()
    {
        var defaultTransport = CreateMockTransport("SendGrid");
        var postmarkTransport = CreateMockTransport("Postmark");
        var options = Options.Create(new PostmarkOptions
        {
            Enabled = true,
            RoutedRecipients = new List<string> { "  routed@example.com  " },
        });

        var router = new EmailTransportRouter(defaultTransport, postmarkTransport, options);

        Assert.Equal("Postmark", router.GetTransportForRecipient("routed@example.com").ProviderName);
        Assert.Equal("Postmark", router.GetTransportForRecipient("  routed@example.com  ").ProviderName);
    }

    [Fact]
    public void EmptyRoutedRecipients_AllGoToDefault()
    {
        var defaultTransport = CreateMockTransport("SendGrid");
        var postmarkTransport = CreateMockTransport("Postmark");
        var options = Options.Create(new PostmarkOptions
        {
            Enabled = true,
            RoutedRecipients = new List<string>(),
        });

        var router = new EmailTransportRouter(defaultTransport, postmarkTransport, options);

        Assert.Equal("SendGrid", router.GetTransportForRecipient("anyone@example.com").ProviderName);
    }

    [Fact]
    public void NullRecipientEmail_ReturnsDefault()
    {
        var defaultTransport = CreateMockTransport("SendGrid");
        var postmarkTransport = CreateMockTransport("Postmark");
        var options = Options.Create(new PostmarkOptions
        {
            Enabled = true,
            RoutedRecipients = new List<string> { "test@example.com" },
        });

        var router = new EmailTransportRouter(defaultTransport, postmarkTransport, options);

        Assert.Equal("SendGrid", router.GetTransportForRecipient(null).ProviderName);
    }

    [Fact]
    public void DefaultTransport_ReturnsDefaultRegardlessOfRouting()
    {
        var defaultTransport = CreateMockTransport("SendGrid");
        var postmarkTransport = CreateMockTransport("Postmark");
        var options = Options.Create(new PostmarkOptions
        {
            Enabled = true,
            RoutedRecipients = new List<string> { "routed@example.com" },
        });

        var router = new EmailTransportRouter(defaultTransport, postmarkTransport, options);

        Assert.Equal("SendGrid", router.DefaultTransport.ProviderName);
    }
}
