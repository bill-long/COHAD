#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;
using Moq;
using Web.Configuration;
using Web.Services;

namespace Web.UnitTests;

public sealed class SesEmailTransportTests
{
    private readonly Mock<IAmazonSimpleEmailServiceV2> _sesClient = new();
    private readonly SesOptions _options = new()
    {
        Enabled = true,
        Region = "us-west-2",
        ConfigurationSetName = "cohad-tracking",
    };

    private SesEmailTransport CreateTransport()
    {
        return new SesEmailTransport(
            _sesClient.Object,
            Options.Create(_options),
            NullLogger<SesEmailTransport>.Instance
        );
    }

    [Fact]
    public void ProviderName_is_Ses()
    {
        var transport = CreateTransport();
        Assert.Equal("Ses", transport.ProviderName);
    }

    [Fact]
    public async Task SendAsync_returns_success_with_message_id()
    {
        _sesClient
            .Setup(c => c.SendEmailAsync(It.IsAny<SendEmailRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SendEmailResponse { MessageId = "ses-msg-123" });

        var transport = CreateTransport();
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Test", "test@cohad.org"));
        message.To.Add(new MailboxAddress("", "user@test.com"));
        message.Subject = "Test";
        message.Body = new TextPart("plain") { Text = "Hello" };

        var result = await transport.SendAsync(message, "job-1", "user@test.com", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("ses-msg-123", result.ProviderMessageId);
        Assert.Equal("Ses", result.ProviderName);
    }

    [Fact]
    public async Task SendAsync_includes_configuration_set()
    {
        SendEmailRequest? capturedRequest = null;
        _sesClient
            .Setup(c => c.SendEmailAsync(It.IsAny<SendEmailRequest>(), It.IsAny<CancellationToken>()))
            .Callback<SendEmailRequest, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new SendEmailResponse { MessageId = "msg-1" });

        var transport = CreateTransport();
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Test", "test@cohad.org"));
        message.To.Add(new MailboxAddress("", "user@test.com"));
        message.Subject = "Test";
        message.Body = new TextPart("plain") { Text = "Hello" };

        await transport.SendAsync(message, "job-1", "user@test.com", CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.Equal("cohad-tracking", capturedRequest!.ConfigurationSetName);
    }

    [Fact]
    public async Task SendAsync_includes_correlation_tags()
    {
        SendEmailRequest? capturedRequest = null;
        _sesClient
            .Setup(c => c.SendEmailAsync(It.IsAny<SendEmailRequest>(), It.IsAny<CancellationToken>()))
            .Callback<SendEmailRequest, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new SendEmailResponse { MessageId = "msg-1" });

        var transport = CreateTransport();
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Test", "test@cohad.org"));
        message.To.Add(new MailboxAddress("", "user@test.com"));
        message.Subject = "Test";
        message.Body = new TextPart("plain") { Text = "Hello" };

        await transport.SendAsync(message, "job-42", "user@test.com", CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.Contains(capturedRequest!.EmailTags, t => t.Name == "cohad_job_id" && t.Value == "job-42");
        Assert.Contains(capturedRequest.EmailTags, t => t.Name == "cohad_email" && t.Value == "user@test.com");
    }

    [Fact]
    public async Task SendAsync_returns_failure_on_exception()
    {
        _sesClient
            .Setup(c => c.SendEmailAsync(It.IsAny<SendEmailRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonSimpleEmailServiceV2Exception("Throttled"));

        var transport = CreateTransport();
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Test", "test@cohad.org"));
        message.To.Add(new MailboxAddress("", "user@test.com"));
        message.Subject = "Test";
        message.Body = new TextPart("plain") { Text = "Hello" };

        var result = await transport.SendAsync(message, "job-1", "user@test.com", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("Ses", result.ProviderName);
        Assert.Contains("Throttled", result.Error);
    }

    [Fact]
    public async Task SendAsync_propagates_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _sesClient
            .Setup(c => c.SendEmailAsync(It.IsAny<SendEmailRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var transport = CreateTransport();
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Test", "test@cohad.org"));
        message.To.Add(new MailboxAddress("", "user@test.com"));
        message.Subject = "Test";
        message.Body = new TextPart("plain") { Text = "Hello" };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            transport.SendAsync(message, "job-1", "user@test.com", cts.Token)
        );
    }
}
