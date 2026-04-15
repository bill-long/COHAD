using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
        private readonly Mock<IEmailDeliveryEventRepository> _deliveryEventRepo = new();
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
                _deliveryEventRepo.Object,
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
        public async Task DeliveredEvent_StoresDeliveryEvent()
        {
            SetupVerifierNotConfigured();

            var body = BuildEventPayload("delivered", TestJobId.ToString(), "user@example.com", "abc123");
            var controller = CreateController(body);

            await controller.HandleEvents();

            _deliveryEventRepo.Verify(
                r =>
                    r.AddAsync(
                        It.Is<EmailDeliveryEvent>(e =>
                            e.JobId == TestJobId
                            && e.Email == "user@example.com"
                            && e.DeliveryStatus == DeliveryStatus.Delivered
                            && e.ProviderMessageId == "abc123"
                            && e.Provider == "SendGrid"
                        )
                    ),
                Times.Once
            );
        }

        [Fact]
        public async Task BounceEvent_StoresDeliveryEvent()
        {
            SetupVerifierNotConfigured();

            var body = BuildEventPayload("bounce", TestJobId.ToString(), "user@example.com");
            var controller = CreateController(body);

            await controller.HandleEvents();

            _deliveryEventRepo.Verify(
                r =>
                    r.AddAsync(
                        It.Is<EmailDeliveryEvent>(e =>
                            e.JobId == TestJobId
                            && e.Email == "user@example.com"
                            && e.DeliveryStatus == DeliveryStatus.Bounced
                            && e.Provider == "SendGrid"
                        )
                    ),
                Times.Once
            );
        }

        [Fact]
        public async Task SpamReportEvent_StoresDeliveryEvent()
        {
            SetupVerifierNotConfigured();

            var body = BuildEventPayload("spamreport", TestJobId.ToString(), "user@example.com");
            var controller = CreateController(body);

            await controller.HandleEvents();

            _deliveryEventRepo.Verify(
                r =>
                    r.AddAsync(
                        It.Is<EmailDeliveryEvent>(e =>
                            e.JobId == TestJobId
                            && e.Email == "user@example.com"
                            && e.DeliveryStatus == DeliveryStatus.SpamReport
                        )
                    ),
                Times.Once
            );
        }

        [Fact]
        public async Task DeferredEvent_StoresDeliveryEvent()
        {
            SetupVerifierNotConfigured();

            var body = BuildEventPayload("deferred", TestJobId.ToString(), "user@example.com");
            var controller = CreateController(body);

            await controller.HandleEvents();

            _deliveryEventRepo.Verify(
                r =>
                    r.AddAsync(
                        It.Is<EmailDeliveryEvent>(e =>
                            e.JobId == TestJobId
                            && e.Email == "user@example.com"
                            && e.DeliveryStatus == DeliveryStatus.Deferred
                        )
                    ),
                Times.Once
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

            _deliveryEventRepo.Verify(r => r.AddAsync(It.IsAny<EmailDeliveryEvent>()), Times.Never);
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

        // ─── Missing signature/timestamp headers ───

        [Fact]
        public async Task RejectsMissingSignatureHeader()
        {
            SetupVerifierConfigured(validSignature: true);
            var controller = CreateController("[]");
            // Timestamp present but no Signature header
            controller.HttpContext.Request.Headers["X-Twilio-Email-Event-Webhook-Timestamp"] = DateTimeOffset
                .UtcNow.ToUnixTimeSeconds()
                .ToString();

            var result = await controller.HandleEvents();

            Assert.IsType<ForbidResult>(result);
        }

        [Fact]
        public async Task RejectsMissingTimestampHeader()
        {
            SetupVerifierConfigured(validSignature: true);
            var controller = CreateController("[]");
            // Signature present but no Timestamp header
            controller.HttpContext.Request.Headers["X-Twilio-Email-Event-Webhook-Signature"] = "validbase64==";

            var result = await controller.HandleEvents();

            Assert.IsType<ForbidResult>(result);
        }

        [Fact]
        public async Task RejectsNonNumericTimestamp()
        {
            SetupVerifierConfigured(validSignature: true);
            var controller = CreateController("[]");
            controller.HttpContext.Request.Headers["X-Twilio-Email-Event-Webhook-Signature"] = "validbase64==";
            controller.HttpContext.Request.Headers["X-Twilio-Email-Event-Webhook-Timestamp"] = "not-a-number";

            var result = await controller.HandleEvents();

            Assert.IsType<ForbidResult>(result);
        }

        // ─── Batch error handling ───

        [Fact]
        public async Task BatchError_Returns500()
        {
            SetupVerifierNotConfigured();
            _deliveryEventRepo
                .Setup(r => r.AddAsync(It.IsAny<EmailDeliveryEvent>()))
                .ThrowsAsync(new InvalidOperationException("Cosmos unavailable"));

            var body = BuildEventPayload("delivered", TestJobId.ToString(), "user@example.com");
            var controller = CreateController(body);

            var result = await controller.HandleEvents();

            // The controller should catch the exception and return 500 so SendGrid retries
            var statusResult = Assert.IsType<StatusCodeResult>(result);
            Assert.Equal(500, statusResult.StatusCode);
        }

        // ─── Invalid JSON body ───

        [Fact]
        public async Task InvalidJsonBody_ReturnsBadRequest()
        {
            SetupVerifierNotConfigured();
            var controller = CreateController("not valid json");

            var result = await controller.HandleEvents();

            Assert.IsType<BadRequestResult>(result);
        }
    }
}
