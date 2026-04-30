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
    public void WhenDisabled_AlwaysReturnsSendGrid()
    {
        var sendGrid = CreateMockTransport("SendGrid");
        var broadcast = CreateMockTransport("PostmarkBroadcast");
        var transactional = CreateMockTransport("PostmarkTransactional");
        var options = Options.Create(new PostmarkOptions
        {
            Enabled = false,
            UsePostmarkAsDefault = true,
        });

        var router = new EmailTransportRouter(sendGrid, broadcast, transactional, options);

        Assert.Equal("SendGrid", router.GetTransportForRecipient("user@example.com", "board").ProviderName);
        Assert.Equal("SendGrid", router.GetTransportForRecipient("user@example.com", "registration").ProviderName);
        Assert.Equal("SendGrid", router.GetDefaultTransport("board").ProviderName);
    }

    [Fact]
    public void WhenEnabled_UsePostmarkFalse_ReturnsSendGrid()
    {
        var sendGrid = CreateMockTransport("SendGrid");
        var broadcast = CreateMockTransport("PostmarkBroadcast");
        var transactional = CreateMockTransport("PostmarkTransactional");
        var options = Options.Create(new PostmarkOptions
        {
            Enabled = true,
            UsePostmarkAsDefault = false,
        });

        var router = new EmailTransportRouter(sendGrid, broadcast, transactional, options);

        Assert.Equal("SendGrid", router.GetTransportForRecipient("user@example.com", "board").ProviderName);
        Assert.Equal("SendGrid", router.GetDefaultTransport("board").ProviderName);
    }

    [Fact]
    public void WhenEnabled_UsePostmarkTrue_BroadcastCategory()
    {
        var sendGrid = CreateMockTransport("SendGrid");
        var broadcast = CreateMockTransport("PostmarkBroadcast");
        var transactional = CreateMockTransport("PostmarkTransactional");
        var options = Options.Create(new PostmarkOptions
        {
            Enabled = true,
            UsePostmarkAsDefault = true,
            TransactionalCategories = new List<string> { "registration", "committee-forward" },
        });

        var router = new EmailTransportRouter(sendGrid, broadcast, transactional, options);

        Assert.Equal("PostmarkBroadcast", router.GetTransportForRecipient("anyone@example.com", "board").ProviderName);
        Assert.Equal("PostmarkBroadcast", router.GetTransportForRecipient("anyone@example.com", "social").ProviderName);
        Assert.Equal("PostmarkBroadcast", router.GetTransportForRecipient("anyone@example.com", "sunshine").ProviderName);
    }

    [Fact]
    public void WhenEnabled_UsePostmarkTrue_TransactionalCategory()
    {
        var sendGrid = CreateMockTransport("SendGrid");
        var broadcast = CreateMockTransport("PostmarkBroadcast");
        var transactional = CreateMockTransport("PostmarkTransactional");
        var options = Options.Create(new PostmarkOptions
        {
            Enabled = true,
            UsePostmarkAsDefault = true,
            TransactionalCategories = new List<string> { "registration", "committee-forward" },
        });

        var router = new EmailTransportRouter(sendGrid, broadcast, transactional, options);

        Assert.Equal("PostmarkTransactional", router.GetTransportForRecipient("anyone@example.com", "registration").ProviderName);
        Assert.Equal("PostmarkTransactional", router.GetTransportForRecipient("anyone@example.com", "committee-forward").ProviderName);
    }

    [Fact]
    public void TransactionalCategory_IsCaseInsensitive()
    {
        var sendGrid = CreateMockTransport("SendGrid");
        var broadcast = CreateMockTransport("PostmarkBroadcast");
        var transactional = CreateMockTransport("PostmarkTransactional");
        var options = Options.Create(new PostmarkOptions
        {
            Enabled = true,
            UsePostmarkAsDefault = true,
            TransactionalCategories = new List<string> { "Registration" },
        });

        var router = new EmailTransportRouter(sendGrid, broadcast, transactional, options);

        Assert.Equal("PostmarkTransactional", router.GetTransportForRecipient("user@example.com", "registration").ProviderName);
        Assert.Equal("PostmarkTransactional", router.GetTransportForRecipient("user@example.com", "REGISTRATION").ProviderName);
    }

    [Fact]
    public void GetDefaultTransport_TransactionalCategory_ReturnsTransactional()
    {
        var sendGrid = CreateMockTransport("SendGrid");
        var broadcast = CreateMockTransport("PostmarkBroadcast");
        var transactional = CreateMockTransport("PostmarkTransactional");
        var options = Options.Create(new PostmarkOptions
        {
            Enabled = true,
            UsePostmarkAsDefault = true,
            TransactionalCategories = new List<string> { "registration", "committee-forward" },
        });

        var router = new EmailTransportRouter(sendGrid, broadcast, transactional, options);

        Assert.Equal("PostmarkTransactional", router.GetDefaultTransport("registration").ProviderName);
        Assert.Equal("PostmarkTransactional", router.GetDefaultTransport("committee-forward").ProviderName);
    }

    [Fact]
    public void GetDefaultTransport_BroadcastCategory_ReturnsBroadcast()
    {
        var sendGrid = CreateMockTransport("SendGrid");
        var broadcast = CreateMockTransport("PostmarkBroadcast");
        var transactional = CreateMockTransport("PostmarkTransactional");
        var options = Options.Create(new PostmarkOptions
        {
            Enabled = true,
            UsePostmarkAsDefault = true,
            TransactionalCategories = new List<string> { "registration" },
        });

        var router = new EmailTransportRouter(sendGrid, broadcast, transactional, options);

        Assert.Equal("PostmarkBroadcast", router.GetDefaultTransport("board").ProviderName);
        Assert.Equal("PostmarkBroadcast", router.GetDefaultTransport(null).ProviderName);
    }

    [Fact]
    public void GetDefaultTransport_WhenDisabled_ReturnsSendGrid()
    {
        var sendGrid = CreateMockTransport("SendGrid");
        var broadcast = CreateMockTransport("PostmarkBroadcast");
        var transactional = CreateMockTransport("PostmarkTransactional");
        var options = Options.Create(new PostmarkOptions
        {
            Enabled = true,
            UsePostmarkAsDefault = false,
        });

        var router = new EmailTransportRouter(sendGrid, broadcast, transactional, options);

        Assert.Equal("SendGrid", router.GetDefaultTransport("registration").ProviderName);
        Assert.Equal("SendGrid", router.GetDefaultTransport("board").ProviderName);
    }

    [Fact]
    public void NullCategory_UsesBroadcast()
    {
        var sendGrid = CreateMockTransport("SendGrid");
        var broadcast = CreateMockTransport("PostmarkBroadcast");
        var transactional = CreateMockTransport("PostmarkTransactional");
        var options = Options.Create(new PostmarkOptions
        {
            Enabled = true,
            UsePostmarkAsDefault = true,
            TransactionalCategories = new List<string> { "registration" },
        });

        var router = new EmailTransportRouter(sendGrid, broadcast, transactional, options);

        Assert.Equal("PostmarkBroadcast", router.GetTransportForRecipient("user@example.com", null).ProviderName);
    }
}
