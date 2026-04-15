#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Web.Configuration;
using Web.Controllers;
using Web.Models;
using Web.Services.Repositories;

namespace Web.UnitTests;

public sealed class SesWebhookControllerTests
{
    private readonly Mock<IEmailDeliveryEventRepository> _deliveryEventRepo = new();
    private readonly Mock<IHttpClientFactory> _httpFactory = new();

    private SesWebhookController CreateController(
        string environment = "MockData",
        List<string>? allowedTopicArns = null
    )
    {
        var env = new Mock<IWebHostEnvironment>();
        env.Setup(e => e.EnvironmentName).Returns(environment);

        var sesOpts = Options.Create(new SesOptions { AllowedTopicArns = allowedTopicArns ?? new List<string>() });

        var controller = new SesWebhookController(
            _deliveryEventRepo.Object,
            _httpFactory.Object,
            sesOpts,
            env.Object,
            NullLogger<SesWebhookController>.Instance
        );

        return controller;
    }

    private void SetRequestBody(ControllerBase controller, string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Request.ContentType = "application/json";
        controller.ControllerContext = new ControllerContext { HttpContext = context };
    }

    [Fact]
    public async Task Returns_BadRequest_for_invalid_json()
    {
        var controller = CreateController();
        SetRequestBody(controller, "not json");

        var result = await controller.HandleNotification();

        Assert.IsType<BadRequestResult>(result);
    }

    [Fact]
    public async Task Returns_BadRequest_for_missing_Type()
    {
        var controller = CreateController();
        SetRequestBody(controller, JsonSerializer.Serialize(new { foo = "bar" }));

        var result = await controller.HandleNotification();

        Assert.IsType<BadRequestResult>(result);
    }

    [Fact]
    public async Task SubscriptionConfirmation_rejects_non_amazonaws_url()
    {
        var controller = CreateController();
        var body = JsonSerializer.Serialize(
            new
            {
                Type = "SubscriptionConfirmation",
                SubscribeURL = "https://evil.com/confirm",
                MessageId = "msg-1",
                Message = "Please confirm",
                Timestamp = DateTime.UtcNow.ToString("o"),
                TopicArn = "arn:aws:sns:us-west-2:123:test",
            }
        );
        SetRequestBody(controller, body);

        var result = await controller.HandleNotification();

        Assert.IsType<BadRequestResult>(result);
    }

    [Fact]
    public async Task Notification_stores_delivery_event_for_delivery()
    {
        var jobId = Guid.NewGuid();

        var sesMessage = JsonSerializer.Serialize(
            new
            {
                notificationType = "Delivery",
                mail = new
                {
                    messageId = "ses-msg-1",
                    tags = new { cohad_job_id = new[] { jobId.ToString() }, cohad_email = new[] { "user@test.com" } },
                },
            }
        );

        var snsMessage = JsonSerializer.Serialize(
            new
            {
                Type = "Notification",
                MessageId = "sns-1",
                Message = sesMessage,
                Timestamp = DateTime.UtcNow.ToString("o"),
                TopicArn = "arn:aws:sns:us-west-2:123:test",
            }
        );

        var controller = CreateController();
        SetRequestBody(controller, snsMessage);

        var result = await controller.HandleNotification();

        Assert.IsType<OkResult>(result);
        _deliveryEventRepo.Verify(
            r =>
                r.AddAsync(
                    It.Is<EmailDeliveryEvent>(e =>
                        e.JobId == jobId
                        && e.Email == "user@test.com"
                        && e.DeliveryStatus == DeliveryStatus.Delivered
                        && e.ProviderMessageId == "ses-msg-1"
                        && e.Provider == "Ses"
                    )
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task Notification_stores_delivery_event_on_permanent_bounce()
    {
        var jobId = Guid.NewGuid();

        var sesMessage = JsonSerializer.Serialize(
            new
            {
                notificationType = "Bounce",
                bounce = new { bounceType = "Permanent" },
                mail = new
                {
                    messageId = "ses-msg-2",
                    tags = new
                    {
                        cohad_job_id = new[] { jobId.ToString() },
                        cohad_email = new[] { "bounced@test.com" },
                    },
                },
            }
        );

        var snsMessage = JsonSerializer.Serialize(
            new
            {
                Type = "Notification",
                MessageId = "sns-2",
                Message = sesMessage,
                Timestamp = DateTime.UtcNow.ToString("o"),
                TopicArn = "arn:aws:sns:us-west-2:123:test",
            }
        );

        var controller = CreateController();
        SetRequestBody(controller, snsMessage);

        await controller.HandleNotification();

        _deliveryEventRepo.Verify(
            r =>
                r.AddAsync(
                    It.Is<EmailDeliveryEvent>(e =>
                        e.JobId == jobId
                        && e.Email == "bounced@test.com"
                        && e.DeliveryStatus == DeliveryStatus.Bounced
                        && e.Provider == "Ses"
                    )
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task Transient_bounce_stores_deferred_event()
    {
        var jobId = Guid.NewGuid();

        var sesMessage = JsonSerializer.Serialize(
            new
            {
                notificationType = "Bounce",
                bounce = new { bounceType = "Transient" },
                mail = new
                {
                    tags = new { cohad_job_id = new[] { jobId.ToString() }, cohad_email = new[] { "user@test.com" } },
                },
            }
        );

        var snsMessage = JsonSerializer.Serialize(
            new
            {
                Type = "Notification",
                MessageId = "sns-3",
                Message = sesMessage,
                Timestamp = DateTime.UtcNow.ToString("o"),
                TopicArn = "arn:aws:sns:us-west-2:123:test",
            }
        );

        var controller = CreateController();
        SetRequestBody(controller, snsMessage);

        await controller.HandleNotification();

        _deliveryEventRepo.Verify(
            r =>
                r.AddAsync(
                    It.Is<EmailDeliveryEvent>(e =>
                        e.JobId == jobId && e.Email == "user@test.com" && e.DeliveryStatus == DeliveryStatus.Deferred
                    )
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task Complaint_stores_spam_report_event()
    {
        var jobId = Guid.NewGuid();

        var sesMessage = JsonSerializer.Serialize(
            new
            {
                notificationType = "Complaint",
                mail = new
                {
                    tags = new
                    {
                        cohad_job_id = new[] { jobId.ToString() },
                        cohad_email = new[] { "complainer@test.com" },
                    },
                },
            }
        );

        var snsMessage = JsonSerializer.Serialize(
            new
            {
                Type = "Notification",
                MessageId = "sns-4",
                Message = sesMessage,
                Timestamp = DateTime.UtcNow.ToString("o"),
                TopicArn = "arn:aws:sns:us-west-2:123:test",
            }
        );

        var controller = CreateController();
        SetRequestBody(controller, snsMessage);

        await controller.HandleNotification();

        _deliveryEventRepo.Verify(
            r =>
                r.AddAsync(
                    It.Is<EmailDeliveryEvent>(e =>
                        e.JobId == jobId
                        && e.Email == "complainer@test.com"
                        && e.DeliveryStatus == DeliveryStatus.SpamReport
                    )
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task Skips_event_with_missing_correlation_tags()
    {
        var sesMessage = JsonSerializer.Serialize(
            new
            {
                notificationType = "Delivery",
                mail = new
                {
                    messageId = "ses-msg-5",
                    tags = new { }, // No cohad tags
                },
            }
        );

        var snsMessage = JsonSerializer.Serialize(
            new
            {
                Type = "Notification",
                MessageId = "sns-5",
                Message = sesMessage,
                Timestamp = DateTime.UtcNow.ToString("o"),
                TopicArn = "arn:aws:sns:us-west-2:123:test",
            }
        );

        var controller = CreateController();
        SetRequestBody(controller, snsMessage);

        var result = await controller.HandleNotification();

        Assert.IsType<OkResult>(result);
        _deliveryEventRepo.Verify(r => r.AddAsync(It.IsAny<EmailDeliveryEvent>()), Times.Never);
    }

    [Fact]
    public void MapSesEventToDeliveryStatus_maps_known_types()
    {
        var deliveryEvent = JsonSerializer.Deserialize<System.Text.Json.JsonElement>("{}");

        Assert.Equal(
            DeliveryStatus.Delivered,
            SesWebhookController.MapSesEventToDeliveryStatus(deliveryEvent, "Delivery")
        );
        Assert.Equal(
            DeliveryStatus.SpamReport,
            SesWebhookController.MapSesEventToDeliveryStatus(deliveryEvent, "Complaint")
        );
        Assert.Equal(
            DeliveryStatus.Rejected,
            SesWebhookController.MapSesEventToDeliveryStatus(deliveryEvent, "Reject")
        );
        Assert.Equal(
            DeliveryStatus.Unknown,
            SesWebhookController.MapSesEventToDeliveryStatus(deliveryEvent, "Unknown")
        );
    }

    // ─── IsValidSnsCertUrl tests ───

    [Theory]
    [InlineData("https://sns.us-east-1.amazonaws.com/SimpleNotificationService-abc123.pem", true)]
    [InlineData("https://sns.eu-west-1.amazonaws.com/SimpleNotificationService-def456.pem", true)]
    [InlineData("https://sns.ap-southeast-2.amazonaws.com/SimpleNotificationService-xyz.pem", true)]
    public void IsValidSnsCertUrl_accepts_valid_aws_urls(string url, bool expected)
    {
        Assert.Equal(expected, SesWebhookController.IsValidSnsCertUrl(url));
    }

    [Theory]
    [InlineData("http://sns.us-east-1.amazonaws.com/SimpleNotificationService-abc.pem")] // http not https
    [InlineData("https://evil.com/SimpleNotificationService-abc.pem")] // wrong host
    [InlineData("https://sns.us-east-1.amazonaws.com/SomethingElse.pem")] // missing SimpleNotificationService prefix
    [InlineData("https://sns.us-east-1.amazonaws.com/SimpleNotificationService-abc.txt")] // not .pem
    [InlineData("https://sns.us-east-1.amazonaws.com/SimpleNotificationService-abc.pem?a=1")] // query string
    [InlineData("https://sns.us-east-1.amazonaws.com/SimpleNotificationService-abc.pem#frag")] // fragment
    [InlineData("https://notsns.us-east-1.amazonaws.com/SimpleNotificationService-abc.pem")] // host doesn't start with sns.
    [InlineData("https://sns.us-east-1.notamazon.com/SimpleNotificationService-abc.pem")] // host doesn't end with amazonaws.com
    [InlineData("https://sns.us-east-1.evilamazonaws.com/SimpleNotificationService-abc.pem")] // suffix-spoofed host
    [InlineData("https://sns.evil.us-east-1.amazonaws.com/SimpleNotificationService-abc.pem")] // extra host segment
    [InlineData("https://sns.ap-southeast-2.amazonaws.com/path/SimpleNotificationService-xyz.pem")] // extra path segment
    [InlineData("https://sns.us-east-1.amazonaws.com/SimpleNotificationService-abc.pem/extra.pem")] // trailing path segment
    [InlineData("not-a-url")] // not a valid URI
    [InlineData("")] // empty string
    public void IsValidSnsCertUrl_rejects_invalid_urls(string url)
    {
        Assert.False(SesWebhookController.IsValidSnsCertUrl(url));
    }

    // ─── BuildSnsStringToSign tests ───

    [Fact]
    public void BuildSnsStringToSign_Notification_includes_correct_fields_in_canonical_order()
    {
        var message = JsonSerializer.Deserialize<JsonElement>(
            JsonSerializer.Serialize(
                new
                {
                    Type = "Notification",
                    MessageId = "msg-123",
                    Message = "Hello world",
                    Timestamp = "2025-01-01T00:00:00.000Z",
                    TopicArn = "arn:aws:sns:us-west-2:123:test",
                }
            )
        );

        var result = SesWebhookController.BuildSnsStringToSign(message);

        // Assert the full canonical string with correct field ordering per AWS SNS spec
        var expected =
            "Message\nHello world\n"
            + "MessageId\nmsg-123\n"
            + "Timestamp\n2025-01-01T00:00:00.000Z\n"
            + "TopicArn\narn:aws:sns:us-west-2:123:test\n"
            + "Type\nNotification\n";
        Assert.Equal(expected, result);
    }

    [Fact]
    public void BuildSnsStringToSign_SubscriptionConfirmation_includes_SubscribeURL_and_Token()
    {
        var message = JsonSerializer.Deserialize<JsonElement>(
            JsonSerializer.Serialize(
                new
                {
                    Type = "SubscriptionConfirmation",
                    MessageId = "msg-456",
                    Message = "Please confirm",
                    SubscribeURL = "https://sns.us-west-2.amazonaws.com/confirm",
                    Token = "tok-789",
                    Timestamp = "2025-01-01T00:00:00.000Z",
                    TopicArn = "arn:aws:sns:us-west-2:123:test",
                }
            )
        );

        var result = SesWebhookController.BuildSnsStringToSign(message);

        // Assert the full canonical string with correct field ordering per AWS SNS spec
        var expected =
            "Message\nPlease confirm\n"
            + "MessageId\nmsg-456\n"
            + "SubscribeURL\nhttps://sns.us-west-2.amazonaws.com/confirm\n"
            + "Timestamp\n2025-01-01T00:00:00.000Z\n"
            + "Token\ntok-789\n"
            + "TopicArn\narn:aws:sns:us-west-2:123:test\n"
            + "Type\nSubscriptionConfirmation\n";
        Assert.Equal(expected, result);
    }

    [Fact]
    public void BuildSnsStringToSign_includes_Subject_when_present()
    {
        var message = JsonSerializer.Deserialize<JsonElement>(
            JsonSerializer.Serialize(
                new
                {
                    Type = "Notification",
                    MessageId = "msg-123",
                    Message = "Hello",
                    Subject = "Test Subject",
                    Timestamp = "2025-01-01T00:00:00.000Z",
                    TopicArn = "arn:aws:sns:us-west-2:123:test",
                }
            )
        );

        var result = SesWebhookController.BuildSnsStringToSign(message);

        var expected =
            "Message\nHello\n"
            + "MessageId\nmsg-123\n"
            + "Subject\nTest Subject\n"
            + "Timestamp\n2025-01-01T00:00:00.000Z\n"
            + "TopicArn\narn:aws:sns:us-west-2:123:test\n"
            + "Type\nNotification\n";
        Assert.Equal(expected, result);
    }

    [Fact]
    public void BuildSnsStringToSign_returns_null_when_Type_missing()
    {
        var message = JsonSerializer.Deserialize<JsonElement>(
            JsonSerializer.Serialize(new { MessageId = "msg-123", Message = "Hello" })
        );

        var result = SesWebhookController.BuildSnsStringToSign(message);

        Assert.Null(result);
    }

    // ─── SNS signature verification (via HandleNotification) ───

    [Fact]
    public async Task Production_rejects_notification_when_signature_verification_fails()
    {
        var controller = CreateController(environment: "Production");
        var body = JsonSerializer.Serialize(
            new
            {
                Type = "Notification",
                MessageId = "sns-1",
                Message = "{}",
                Timestamp = DateTime.UtcNow.ToString("o"),
                TopicArn = "arn:aws:sns:us-west-2:123:test",
                // No SigningCertURL or Signature — verification will fail
            }
        );
        SetRequestBody(controller, body);

        var result = await controller.HandleNotification();

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task DevMode_accepts_notification_when_signature_verification_fails()
    {
        var controller = CreateController(environment: "MockData");
        var body = JsonSerializer.Serialize(
            new
            {
                Type = "Notification",
                MessageId = "sns-1",
                Message = "{}",
                Timestamp = DateTime.UtcNow.ToString("o"),
                TopicArn = "arn:aws:sns:us-west-2:123:test",
                // No SigningCertURL — verification will fail but dev mode accepts
            }
        );
        SetRequestBody(controller, body);

        var result = await controller.HandleNotification();

        // Should accept in dev mode and return Ok (the inner SES message has no correlation tags so it's skipped)
        Assert.IsType<OkResult>(result);
    }

    // ─── SNS timestamp freshness tests ───

    [Fact]
    public async Task DevMode_accepts_notification_without_Timestamp()
    {
        var controller = CreateController(environment: "MockData");
        var body = JsonSerializer.Serialize(
            new
            {
                Type = "Notification",
                MessageId = "sns-ts-1",
                Message = "{}",
                TopicArn = "arn:aws:sns:us-west-2:123:test",
                // No Timestamp — dev mode tolerates this
            }
        );
        SetRequestBody(controller, body);

        var result = await controller.HandleNotification();
        Assert.IsType<OkResult>(result);
    }

    [Fact]
    public async Task DevMode_accepts_notification_with_invalid_Timestamp()
    {
        var controller = CreateController(environment: "MockData");
        var body = JsonSerializer.Serialize(
            new
            {
                Type = "Notification",
                MessageId = "sns-ts-2",
                Message = "{}",
                Timestamp = "not-a-date",
                TopicArn = "arn:aws:sns:us-west-2:123:test",
            }
        );
        SetRequestBody(controller, body);

        var result = await controller.HandleNotification();
        Assert.IsType<OkResult>(result);
    }

    [Fact]
    public async Task Rejects_notification_with_timestamp_too_far_in_the_future()
    {
        var controller = CreateController();
        var futureTime = DateTime.UtcNow.AddMinutes(10); // Beyond the 5-min future tolerance
        var body = JsonSerializer.Serialize(
            new
            {
                Type = "Notification",
                MessageId = "sns-ts-3",
                Message = "{}",
                Timestamp = futureTime.ToString("o"),
                TopicArn = "arn:aws:sns:us-west-2:123:test",
            }
        );
        SetRequestBody(controller, body);

        var result = await controller.HandleNotification();

        Assert.IsType<BadRequestResult>(result);
    }

    [Fact]
    public async Task Rejects_notification_with_timestamp_older_than_MaxMessageAge()
    {
        var controller = CreateController();
        var oldTime = DateTime.UtcNow.AddMinutes(-15); // Beyond the 10-min max age
        var body = JsonSerializer.Serialize(
            new
            {
                Type = "Notification",
                MessageId = "sns-ts-4",
                Message = "{}",
                Timestamp = oldTime.ToString("o"),
                TopicArn = "arn:aws:sns:us-west-2:123:test",
            }
        );
        SetRequestBody(controller, body);

        var result = await controller.HandleNotification();

        Assert.IsType<BadRequestResult>(result);
    }

    [Fact]
    public async Task Accepts_notification_with_timestamp_within_valid_window()
    {
        var controller = CreateController();
        var recentTime = DateTime.UtcNow.AddSeconds(-30); // Recent, within the 10-min window
        var body = JsonSerializer.Serialize(
            new
            {
                Type = "Notification",
                MessageId = "sns-ts-5",
                Message = "{}",
                Timestamp = recentTime.ToString("o"),
                TopicArn = "arn:aws:sns:us-west-2:123:test",
            }
        );
        SetRequestBody(controller, body);

        var result = await controller.HandleNotification();

        // Should pass timestamp validation and proceed (Ok because inner SES message has no correlation tags)
        Assert.IsType<OkResult>(result);
    }

    // ─── TopicArn allowlist tests ───

    [Fact]
    public async Task Rejects_notification_with_unlisted_TopicArn()
    {
        var controller = CreateController(
            allowedTopicArns: new List<string> { "arn:aws:sns:us-west-2:123:allowed-topic" }
        );
        var body = JsonSerializer.Serialize(
            new
            {
                Type = "Notification",
                MessageId = "sns-arn-1",
                Message = "{}",
                Timestamp = DateTime.UtcNow.ToString("o"),
                TopicArn = "arn:aws:sns:us-west-2:123:rogue-topic",
            }
        );
        SetRequestBody(controller, body);

        var result = await controller.HandleNotification();

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task Rejects_notification_with_missing_TopicArn_when_allowlist_configured()
    {
        var controller = CreateController(
            allowedTopicArns: new List<string> { "arn:aws:sns:us-west-2:123:allowed-topic" }
        );
        var body = JsonSerializer.Serialize(
            new
            {
                Type = "Notification",
                MessageId = "sns-arn-2",
                Message = "{}",
                Timestamp = DateTime.UtcNow.ToString("o"),
                // No TopicArn
            }
        );
        SetRequestBody(controller, body);

        var result = await controller.HandleNotification();

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task Accepts_notification_with_listed_TopicArn()
    {
        var controller = CreateController(
            allowedTopicArns: new List<string> { "arn:aws:sns:us-west-2:123:allowed-topic" }
        );
        var body = JsonSerializer.Serialize(
            new
            {
                Type = "Notification",
                MessageId = "sns-arn-3",
                Message = "{}",
                Timestamp = DateTime.UtcNow.ToString("o"),
                TopicArn = "arn:aws:sns:us-west-2:123:allowed-topic",
            }
        );
        SetRequestBody(controller, body);

        var result = await controller.HandleNotification();

        // Should pass ARN check and proceed (Ok because inner SES message has no correlation tags)
        Assert.IsType<OkResult>(result);
    }

    [Fact]
    public async Task Empty_allowlist_accepts_any_TopicArn()
    {
        var controller = CreateController(allowedTopicArns: new List<string>());
        var body = JsonSerializer.Serialize(
            new
            {
                Type = "Notification",
                MessageId = "sns-arn-4",
                Message = "{}",
                Timestamp = DateTime.UtcNow.ToString("o"),
                TopicArn = "arn:aws:sns:us-west-2:123:any-topic",
            }
        );
        SetRequestBody(controller, body);

        var result = await controller.HandleNotification();

        Assert.IsType<OkResult>(result);
    }

    // ─── SES webhook stores delivery events (no direct job mutation) ───

    [Fact]
    public async Task Notification_stores_event_for_delivery_with_pending_recipient()
    {
        var jobId = Guid.NewGuid();

        var sesMessage = JsonSerializer.Serialize(
            new
            {
                notificationType = "Delivery",
                mail = new
                {
                    messageId = "ses-msg-fix-1",
                    tags = new { cohad_job_id = new[] { jobId.ToString() }, cohad_email = new[] { "user@test.com" } },
                },
            }
        );

        var snsMessage = JsonSerializer.Serialize(
            new
            {
                Type = "Notification",
                MessageId = "sns-fix-1",
                Message = sesMessage,
                Timestamp = DateTime.UtcNow.ToString("o"),
                TopicArn = "arn:aws:sns:us-west-2:123:test",
            }
        );

        var controller = CreateController();
        SetRequestBody(controller, snsMessage);

        await controller.HandleNotification();

        _deliveryEventRepo.Verify(
            r =>
                r.AddAsync(
                    It.Is<EmailDeliveryEvent>(e =>
                        e.JobId == jobId && e.Email == "user@test.com" && e.DeliveryStatus == DeliveryStatus.Delivered
                    )
                ),
            Times.Once
        );
    }
}
