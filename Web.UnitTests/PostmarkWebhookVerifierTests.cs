using Microsoft.Extensions.Options;
using Web.Configuration;
using Web.Services;
using Xunit;

namespace Web.UnitTests;

public sealed class PostmarkWebhookVerifierTests
{
    [Fact]
    public void IsConfigured_FalseWhenTokenEmpty()
    {
        var options = Options.Create(new PostmarkOptions { WebhookToken = "" });
        var verifier = new PostmarkWebhookVerifier(options);
        Assert.False(verifier.IsConfigured);
    }

    [Fact]
    public void IsConfigured_TrueWhenTokenSet()
    {
        var options = Options.Create(new PostmarkOptions { WebhookToken = "my-secret-token" });
        var verifier = new PostmarkWebhookVerifier(options);
        Assert.True(verifier.IsConfigured);
    }

    [Fact]
    public void Verify_ReturnsTrueForMatchingToken()
    {
        var options = Options.Create(new PostmarkOptions { WebhookToken = "my-secret-token" });
        var verifier = new PostmarkWebhookVerifier(options);
        Assert.True(verifier.Verify("my-secret-token"));
    }

    [Fact]
    public void Verify_ReturnsFalseForWrongToken()
    {
        var options = Options.Create(new PostmarkOptions { WebhookToken = "my-secret-token" });
        var verifier = new PostmarkWebhookVerifier(options);
        Assert.False(verifier.Verify("wrong-token"));
    }

    [Fact]
    public void Verify_ReturnsFalseForEmptyInput()
    {
        var options = Options.Create(new PostmarkOptions { WebhookToken = "my-secret-token" });
        var verifier = new PostmarkWebhookVerifier(options);
        Assert.False(verifier.Verify(""));
    }

    [Fact]
    public void Verify_ReturnsFalseWhenNotConfigured()
    {
        var options = Options.Create(new PostmarkOptions { WebhookToken = "" });
        var verifier = new PostmarkWebhookVerifier(options);
        Assert.False(verifier.Verify("any-token"));
    }
}
