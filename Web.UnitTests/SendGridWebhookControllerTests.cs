using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Web.Controllers;
using Web.Models;
using Web.Services;
using Web.Services.Repositories;
using Xunit;

namespace Web.UnitTests
{
    public class SendGridWebhookControllerTests
    {
        private readonly Mock<ISendGridWebhookVerifier> _verifier = new();
        private readonly Mock<IEmailJobRepository> _jobRepo = new();
        private readonly Mock<IEmailDeliveryActionService> _deliveryAction = new();
        private readonly Mock<IWebHostEnvironment> _env = new();

        public SendGridWebhookControllerTests()
        {
            // Default to MockData environment (development-like, accepts unconfigured verifier)
            _env.Setup(e => e.EnvironmentName).Returns("MockData");
        }

        private SendGridWebhookController CreateController(string body)
        {
            var controller = new SendGridWebhookController(
                _verifier.Object,
                _jobRepo.Object,
                _deliveryAction.Object,
                _env.Object,
                NullLogger<SendGridWebhookController>.Instance
            );

            var httpContext = new DefaultHttpContext();
            httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
            httpContext.Request.ContentType = "application/json";
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

            return controller;
        }

        private void SetupVerifierNotConfigured()
        {
            _verifier.Setup(v => v.IsConfigured).Returns(false);
        }

        private void SetupVerifierConfigured(bool validSignature = true)
        {
            _verifier.Setup(v => v.IsConfigured).Returns(true);
            _verifier
                .Setup(v => v.Verify(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns(validSignature);
        }

        private static readonly Guid TestJobId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        private EmailJob CreateTestJob(string recipientEmail = "user@example.com")
        {
            return new EmailJob
            {
                Id = TestJobId,
                Status = EmailJobStatus.Completed,
                Category = "board",
                Recipients = new List<EmailJobRecipient>
                {
                    new()
                    {
                        Email = recipientEmail,
                        HomeId = Guid.NewGuid(),
                        Status = EmailJobRecipientStatus.Sent,
                        SentUtc = DateTime.UtcNow,
                    },
                },
            };
        }

        private string BuildEventPayload(
            string eventType,
            string? jobId = null,
            string? email = null,
            string? sgMessageId = null
        )
        {
            var evt = new Dictionary<string, object> { ["event"] = eventType, ["email"] = email ?? "user@example.com" };
            if (jobId != null)
                evt["cohad_job_id"] = jobId;
            if (email != null)
                evt["cohad_email"] = email;
            if (sgMessageId != null)
                evt["sg_message_id"] = sgMessageId;

            return JsonSerializer.Serialize(new[] { evt });
        }

        // ─── Signature verification ───

        [Fact]
        public async Task RejectsBadSignature()
        {
            SetupVerifierConfigured(validSignature: false);
            var controller = CreateController("[]");
            controller.HttpContext.Request.Headers["X-Twilio-Email-Event-Webhook-Signature"] = "badsig";
            controller.HttpContext.Request.Headers["X-Twilio-Email-Event-Webhook-Timestamp"] = "12345";

            var result = await controller.HandleEvents();

            Assert.IsType<ForbidResult>(result);
        }

        [Fact]
        public async Task AcceptsWhenVerifierNotConfigured_InDevelopment()
        {
            SetupVerifierNotConfigured();
            var controller = CreateController("[]");

            var result = await controller.HandleEvents();

            Assert.IsType<OkResult>(result);
        }

        [Fact]
        public async Task RejectsWhenVerifierNotConfigured_InProduction()
        {
            _env.Setup(e => e.EnvironmentName).Returns("Production");
            SetupVerifierNotConfigured();
            var controller = CreateController("[]");

            var result = await controller.HandleEvents();

            Assert.IsType<ForbidResult>(result);
        }

        [Fact]
        public async Task AcceptsValidSignature()
        {
            SetupVerifierConfigured(validSignature: true);
            var controller = CreateController("[]");
            controller.HttpContext.Request.Headers["X-Twilio-Email-Event-Webhook-Signature"] = "validbase64==";
            controller.HttpContext.Request.Headers["X-Twilio-Email-Event-Webhook-Timestamp"] = DateTimeOffset
                .UtcNow.ToUnixTimeSeconds()
                .ToString();

            var result = await controller.HandleEvents();

            Assert.IsType<OkResult>(result);
        }

        [Fact]
        public async Task RejectsStaleTimestamp()
        {
            SetupVerifierConfigured(validSignature: true);
            var controller = CreateController("[]");
            controller.HttpContext.Request.Headers["X-Twilio-Email-Event-Webhook-Signature"] = "validbase64==";
            // Timestamp from 2020 — well outside the 5-minute window
            controller.HttpContext.Request.Headers["X-Twilio-Email-Event-Webhook-Timestamp"] = "1577836800";

            var result = await controller.HandleEvents();

            Assert.IsType<ForbidResult>(result);
        }

        // ─── Event processing ───

        [Fact]
        public async Task DeliveredEvent_UpdatesRecipientStatus()
        {
            SetupVerifierNotConfigured();
            var job = CreateTestJob();
            _jobRepo.Setup(r => r.GetByIdAsync(TestJobId)).ReturnsAsync(job);

            var body = BuildEventPayload("delivered", TestJobId.ToString(), "user@example.com", "abc123");
            var controller = CreateController(body);

            await controller.HandleEvents();

            Assert.Equal(DeliveryStatus.Delivered, job.Recipients[0].DeliveryStatus);
            Assert.NotNull(job.Recipients[0].DeliveryStatusUpdatedUtc);
            Assert.Equal("abc123", job.Recipients[0].ProviderMessageId);
            _jobRepo.Verify(r => r.UpdateAsync(job), Times.Once);
        }

        [Fact]
        public async Task BounceEvent_UpdatesStatusAndTriggersOptOut()
        {
            SetupVerifierNotConfigured();
            var job = CreateTestJob();
            _jobRepo.Setup(r => r.GetByIdAsync(TestJobId)).ReturnsAsync(job);

            var body = BuildEventPayload("bounce", TestJobId.ToString(), "user@example.com");
            var controller = CreateController(body);

            await controller.HandleEvents();

            Assert.Equal(DeliveryStatus.Bounced, job.Recipients[0].DeliveryStatus);
            _deliveryAction.Verify(
                s => s.ProcessDeliveryEventAsync("user@example.com", DeliveryStatus.Bounced, "board"),
                Times.Once
            );
        }

        [Fact]
        public async Task SpamReportEvent_TriggersOptOut()
        {
            SetupVerifierNotConfigured();
            var job = CreateTestJob();
            _jobRepo.Setup(r => r.GetByIdAsync(TestJobId)).ReturnsAsync(job);

            var body = BuildEventPayload("spamreport", TestJobId.ToString(), "user@example.com");
            var controller = CreateController(body);

            await controller.HandleEvents();

            Assert.Equal(DeliveryStatus.SpamReport, job.Recipients[0].DeliveryStatus);
            _deliveryAction.Verify(
                s => s.ProcessDeliveryEventAsync("user@example.com", DeliveryStatus.SpamReport, "board"),
                Times.Once
            );
        }

        [Fact]
        public async Task DeferredEvent_DoesNotTriggerOptOut()
        {
            SetupVerifierNotConfigured();
            var job = CreateTestJob();
            _jobRepo.Setup(r => r.GetByIdAsync(TestJobId)).ReturnsAsync(job);

            var body = BuildEventPayload("deferred", TestJobId.ToString(), "user@example.com");
            var controller = CreateController(body);

            await controller.HandleEvents();

            Assert.Equal(DeliveryStatus.Deferred, job.Recipients[0].DeliveryStatus);
            _deliveryAction.Verify(
                s => s.ProcessDeliveryEventAsync(It.IsAny<string>(), It.IsAny<DeliveryStatus>(), It.IsAny<string>()),
                Times.Never
            );
        }

        // ─── Correlation / idempotency ───

        [Fact]
        public async Task MissingCorrelationArgs_SkipsEvent()
        {
            SetupVerifierNotConfigured();
            var body = JsonSerializer.Serialize(new[] { new { @event = "delivered", email = "x@example.com" } });
            var controller = CreateController(body);

            await controller.HandleEvents();

            _jobRepo.Verify(r => r.GetByIdAsync(It.IsAny<Guid>()), Times.Never);
        }

        [Fact]
        public async Task UnknownJob_SkipsGracefully()
        {
            SetupVerifierNotConfigured();
            _jobRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>())).ReturnsAsync((EmailJob?)null);

            var body = BuildEventPayload("delivered", TestJobId.ToString(), "user@example.com");
            var controller = CreateController(body);

            var result = await controller.HandleEvents();

            Assert.IsType<OkResult>(result);
            _jobRepo.Verify(r => r.UpdateAsync(It.IsAny<EmailJob>()), Times.Never);
        }

        [Fact]
        public async Task UnknownRecipient_SkipsGracefully()
        {
            SetupVerifierNotConfigured();
            var job = CreateTestJob("other@example.com");
            _jobRepo.Setup(r => r.GetByIdAsync(TestJobId)).ReturnsAsync(job);

            var body = BuildEventPayload("delivered", TestJobId.ToString(), "notfound@example.com");
            var controller = CreateController(body);

            await controller.HandleEvents();

            _jobRepo.Verify(r => r.UpdateAsync(It.IsAny<EmailJob>()), Times.Never);
        }

        // ─── Status severity ───

        [Theory]
        [InlineData(DeliveryStatus.Unknown, DeliveryStatus.Delivered, true)]
        [InlineData(DeliveryStatus.Delivered, DeliveryStatus.Bounced, true)]
        [InlineData(DeliveryStatus.Bounced, DeliveryStatus.Delivered, false)]
        [InlineData(DeliveryStatus.Deferred, DeliveryStatus.Delivered, true)]
        [InlineData(DeliveryStatus.Delivered, DeliveryStatus.Deferred, false)]
        [InlineData(DeliveryStatus.SpamReport, DeliveryStatus.Delivered, false)]
        public void ShouldUpdateDeliveryStatus_FollowsSeverityOrder(
            DeliveryStatus current,
            DeliveryStatus incoming,
            bool expected
        )
        {
            Assert.Equal(expected, DeliveryStatusHelper.ShouldUpdate(current, incoming));
        }

        // ─── Event mapping ───

        [Theory]
        [InlineData("delivered", DeliveryStatus.Delivered)]
        [InlineData("bounce", DeliveryStatus.Bounced)]
        [InlineData("dropped", DeliveryStatus.Rejected)]
        [InlineData("spamreport", DeliveryStatus.SpamReport)]
        [InlineData("deferred", DeliveryStatus.Deferred)]
        [InlineData("unknownevent", DeliveryStatus.Unknown)]
        public void MapEventToDeliveryStatus_MapsCorrectly(string eventType, DeliveryStatus expected)
        {
            Assert.Equal(expected, SendGridWebhookController.MapEventToDeliveryStatus(eventType));
        }
    }
}
