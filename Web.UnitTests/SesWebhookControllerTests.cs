#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
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
using Web.Services;
using Web.Services.Repositories;

namespace Web.UnitTests;

public sealed class SesWebhookControllerTests
{
    private readonly Mock<IEmailJobRepository> _jobRepo = new();
    private readonly Mock<IEmailDeliveryActionService> _deliveryAction = new();
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
            _jobRepo.Object,
            _deliveryAction.Object,
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
    public async Task Notification_updates_delivery_status_for_delivery_event()
    {
        var jobId = Guid.NewGuid();
        var job = new EmailJob
        {
            Id = jobId,
            Category = "board",
            Recipients = new List<EmailJobRecipient>
            {
                new()
                {
                    Email = "user@test.com",
                    Status = EmailJobRecipientStatus.Sent,
                    Provider = "Ses",
                },
            },
        };
        _jobRepo.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(job);

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
        Assert.Equal(DeliveryStatus.Delivered, job.Recipients[0].DeliveryStatus);
        Assert.Equal("ses-msg-1", job.Recipients[0].ProviderMessageId);
        _jobRepo.Verify(r => r.UpdateAsync(job), Times.Once);
    }

    [Fact]
    public async Task Notification_triggers_opt_out_on_permanent_bounce()
    {
        var jobId = Guid.NewGuid();
        var job = new EmailJob
        {
            Id = jobId,
            Category = "board",
            Recipients = new List<EmailJobRecipient>
            {
                new()
                {
                    Email = "bounced@test.com",
                    Status = EmailJobRecipientStatus.Sent,
                    Provider = "Ses",
                },
            },
        };
        _jobRepo.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(job);

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

        Assert.Equal(DeliveryStatus.Bounced, job.Recipients[0].DeliveryStatus);
        _deliveryAction.Verify(
            d => d.ProcessDeliveryEventAsync("bounced@test.com", DeliveryStatus.Bounced, "board"),
            Times.Once
        );
    }

    [Fact]
    public async Task Transient_bounce_maps_to_Deferred()
    {
        var jobId = Guid.NewGuid();
        var job = new EmailJob
        {
            Id = jobId,
            Category = "board",
            Recipients = new List<EmailJobRecipient>
            {
                new()
                {
                    Email = "user@test.com",
                    Status = EmailJobRecipientStatus.Sent,
                    Provider = "Ses",
                },
            },
        };
        _jobRepo.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(job);

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

        Assert.Equal(DeliveryStatus.Deferred, job.Recipients[0].DeliveryStatus);
        // Deferred is not a hard bounce — no opt-out
        _deliveryAction.Verify(
            d => d.ProcessDeliveryEventAsync(It.IsAny<string>(), It.IsAny<DeliveryStatus>(), It.IsAny<string?>()),
            Times.Never
        );
    }

    [Fact]
    public async Task Complaint_maps_to_SpamReport_and_triggers_opt_out()
    {
        var jobId = Guid.NewGuid();
        var job = new EmailJob
        {
            Id = jobId,
            Category = "welcome",
            Recipients = new List<EmailJobRecipient>
            {
                new()
                {
                    Email = "complainer@test.com",
                    Status = EmailJobRecipientStatus.Sent,
                    Provider = "Ses",
                },
            },
        };
        _jobRepo.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(job);

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

        Assert.Equal(DeliveryStatus.SpamReport, job.Recipients[0].DeliveryStatus);
        _deliveryAction.Verify(
            d => d.ProcessDeliveryEventAsync("complainer@test.com", DeliveryStatus.SpamReport, "welcome"),
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
        _jobRepo.Verify(r => r.GetByIdAsync(It.IsAny<Guid>()), Times.Never);
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
}
