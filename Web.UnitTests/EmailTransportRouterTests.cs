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
        var broadcast = CreateMockTransport("PostmarkBroadcast");
        var transactional = CreateMockTransport("PostmarkTransactional");
        var options = Options.Create(new PostmarkOptions
        {
            Enabled = false,
            RoutedRecipients = new List<string> { "test@example.com" },
        });

        var router = new EmailTransportRouter(defaultTransport, broadcast, transactional, options);

        Assert.Equal("SendGrid", router.GetTransportForRecipient("test@example.com", "board").ProviderName);
        Assert.Equal("SendGrid", router.GetTransportForRecipient("other@example.com", "board").ProviderName);
    }

    [Fact]
    public void WhenEnabled_RoutesBroadcastCategory()
    {
        var defaultTransport = CreateMockTransport("SendGrid");
        var broadcast = CreateMockTransport("PostmarkBroadcast");
        var transactional = CreateMockTransport("PostmarkTransactional");
        var options = Options.Create(new PostmarkOptions
        {
            Enabled = true,
            RoutedRecipients = new List<string> { "routed@example.com" },
            TransactionalCategories = new List<string> { "registration", "committee-forward" },
        });

        var router = new EmailTransportRouter(defaultTransport, broadcast, transactional, options);

        Assert.Equal("PostmarkBroadcast", router.GetTransportForRecipient("routed@example.com", "board").ProviderName);
        Assert.Equal("PostmarkBroadcast", router.GetTransportForRecipient("routed@example.com", "social").ProviderName);
    }

    [Fact]
    public void WhenEnabled_RoutesTransactionalCategory()
    {
        var defaultTransport = CreateMockTransport("SendGrid");
        var broadcast = CreateMockTransport("PostmarkBroadcast");
        var transactional = CreateMockTransport("PostmarkTransactional");
        var options = Options.Create(new PostmarkOptions
        {
            Enabled = true,
            RoutedRecipients = new List<string> { "routed@example.com" },
            TransactionalCategories = new List<string> { "registration", "committee-forward" },
        });

        var router = new EmailTransportRouter(defaultTransport, broadcast, transactional, options);

        Assert.Equal("PostmarkTransactional", router.GetTransportForRecipient("routed@example.com", "registration").ProviderName);
        Assert.Equal("PostmarkTransactional", router.GetTransportForRecipient("routed@example.com", "committee-forward").ProviderName);
    }

    [Fact]
    public void NonRoutedRecipient_AlwaysUsesDefault()
    {
        var defaultTransport = CreateMockTransport("SendGrid");
        var broadcast = CreateMockTransport("PostmarkBroadcast");
        var transactional = CreateMockTransport("PostmarkTransactional");
        var options = Options.Create(new PostmarkOptions
        {
            Enabled = true,
            RoutedRecipients = new List<string> { "routed@example.com" },
        });

        var router = new EmailTransportRouter(defaultTransport, broadcast, transactional, options);

        Assert.Equal("SendGrid", router.GetTransportForRecipient("other@example.com", "board").ProviderName);
        Assert.Equal("SendGrid", router.GetTransportForRecipient("other@example.com", "registration").ProviderName);
    }

    [Fact]
    public void Routing_IsCaseInsensitive()
    {
        var defaultTransport = CreateMockTransport("SendGrid");
        var broadcast = CreateMockTransport("PostmarkBroadcast");
        var transactional = CreateMockTransport("PostmarkTransactional");
        var options = Options.Create(new PostmarkOptions
        {
            Enabled = true,
            RoutedRecipients = new List<string> { "User@Example.COM" },
            TransactionalCategories = new List<string> { "Registration" },
        });

        var router = new EmailTransportRouter(defaultTransport, broadcast, transactional, options);

        Assert.Equal("PostmarkBroadcast", router.GetTransportForRecipient("user@example.com", "board").ProviderName);
        Assert.Equal("PostmarkTransactional", router.GetTransportForRecipient("USER@EXAMPLE.COM", "registration").ProviderName);
    }

    [Fact]
    public void Routing_TrimsWhitespace()
    {
        var defaultTransport = CreateMockTransport("SendGrid");
        var broadcast = CreateMockTransport("PostmarkBroadcast");
        var transactional = CreateMockTransport("PostmarkTransactional");
        var options = Options.Create(new PostmarkOptions
        {
            Enabled = true,
            RoutedRecipients = new List<string> { "  routed@example.com  " },
        });

        var router = new EmailTransportRouter(defaultTransport, broadcast, transactional, options);

        Assert.Equal("PostmarkBroadcast", router.GetTransportForRecipient("routed@example.com", "board").ProviderName);
        Assert.Equal("PostmarkBroadcast", router.GetTransportForRecipient("  routed@example.com  ", "board").ProviderName);
    }

    [Fact]
    public void EmptyRoutedRecipients_AllGoToDefault()
    {
        var defaultTransport = CreateMockTransport("SendGrid");
        var broadcast = CreateMockTransport("PostmarkBroadcast");
        var transactional = CreateMockTransport("PostmarkTransactional");
        var options = Options.Create(new PostmarkOptions
        {
            Enabled = true,
            RoutedRecipients = new List<string>(),
        });

        var router = new EmailTransportRouter(defaultTransport, broadcast, transactional, options);

        Assert.Equal("SendGrid", router.GetTransportForRecipient("anyone@example.com", "board").ProviderName);
    }

    [Fact]
    public void NullRecipientEmail_ReturnsDefault()
    {
        var defaultTransport = CreateMockTransport("SendGrid");
        var broadcast = CreateMockTransport("PostmarkBroadcast");
        var transactional = CreateMockTransport("PostmarkTransactional");
        var options = Options.Create(new PostmarkOptions
        {
            Enabled = true,
            RoutedRecipients = new List<string> { "test@example.com" },
        });

        var router = new EmailTransportRouter(defaultTransport, broadcast, transactional, options);

        Assert.Equal("SendGrid", router.GetTransportForRecipient(null, "board").ProviderName);
    }

    [Fact]
    public void DefaultTransport_ReturnsDefaultRegardlessOfRouting()
    {
        var defaultTransport = CreateMockTransport("SendGrid");
        var broadcast = CreateMockTransport("PostmarkBroadcast");
        var transactional = CreateMockTransport("PostmarkTransactional");
        var options = Options.Create(new PostmarkOptions
        {
            Enabled = true,
            RoutedRecipients = new List<string> { "routed@example.com" },
        });

        var router = new EmailTransportRouter(defaultTransport, broadcast, transactional, options);

        Assert.Equal("SendGrid", router.DefaultTransport.ProviderName);
    }
}
