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
    public class PostmarkWebhookControllerTests
    {
        private readonly Mock<IPostmarkWebhookVerifier> _verifier = new();
        private readonly Mock<IEmailDeliveryEventRepository> _deliveryEventRepo = new();
        private readonly Mock<IWebHostEnvironment> _env = new();

        public PostmarkWebhookControllerTests()
        {
            _env.Setup(e => e.EnvironmentName).Returns("MockData");
            _deliveryEventRepo
                .Setup(r => r.AddAsync(It.IsAny<EmailDeliveryEvent>()))
                .Returns(Task.CompletedTask);
        }

        private PostmarkWebhookController CreateController(string body)
        {
            var controller = new PostmarkWebhookController(
                _verifier.Object,
                _deliveryEventRepo.Object,
                _env.Object,
                NullLogger<PostmarkWebhookController>.Instance
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

        private void SetupVerifierConfigured(bool valid = true)
        {
            _verifier.Setup(v => v.IsConfigured).Returns(true);
            _verifier.Setup(v => v.Verify(It.IsAny<string>())).Returns(valid);
        }

        private static readonly Guid TestJobId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        private static string BuildDeliveryPayload(
            string? jobId = null,
            string? recipient = null,
            string? messageId = null
        )
        {
            var payload = new Dictionary<string, object>
            {
                ["RecordType"] = "Delivery",
                ["Recipient"] = recipient ?? "user@example.com",
                ["MessageID"] = messageId ?? "test-msg-id",
                ["Metadata"] = new Dictionary<string, string>
                {
                    ["cohad_job_id"] = jobId ?? TestJobId.ToString(),
                },
            };
            return JsonSerializer.Serialize(payload);
        }

        private static string BuildBouncePayload(
            string bounceType = "HardBounce",
            string? jobId = null,
            string? email = null,
            long id = 42
        )
        {
            var payload = new Dictionary<string, object>
            {
                ["RecordType"] = "Bounce",
                ["Type"] = bounceType,
                ["Email"] = email ?? "user@example.com",
                ["MessageID"] = "test-msg-id",
                ["ID"] = id,
                ["Metadata"] = new Dictionary<string, string>
                {
                    ["cohad_job_id"] = jobId ?? TestJobId.ToString(),
                },
            };
            return JsonSerializer.Serialize(payload);
        }

        private static string BuildSpamComplaintPayload(
            string? jobId = null,
            string? email = null,
            long id = 99
        )
        {
            var payload = new Dictionary<string, object>
            {
                ["RecordType"] = "SpamComplaint",
                ["Email"] = email ?? "user@example.com",
                ["MessageID"] = "test-msg-id",
                ["ID"] = id,
                ["Metadata"] = new Dictionary<string, string>
                {
                    ["cohad_job_id"] = jobId ?? TestJobId.ToString(),
                },
            };
            return JsonSerializer.Serialize(payload);
        }

        // ─── Token verification ───

        [Fact]
        public async Task RejectsWhenTokenNotConfiguredInProduction()
        {
            _env.Setup(e => e.EnvironmentName).Returns("Production");
            SetupVerifierNotConfigured();

            var controller = CreateController(BuildDeliveryPayload());
            var result = await controller.HandleEvent();

            Assert.IsType<ForbidResult>(result);
        }

        [Fact]
        public async Task AcceptsWhenTokenNotConfiguredInDevelopment()
        {
            SetupVerifierNotConfigured();

            var controller = CreateController(BuildDeliveryPayload());
            var result = await controller.HandleEvent();

            Assert.IsType<OkResult>(result);
        }

        [Fact]
        public async Task RejectsWhenTokenInvalid()
        {
            SetupVerifierConfigured(valid: false);

            var controller = CreateController(BuildDeliveryPayload());
            controller.ControllerContext.HttpContext.Request.Headers["X-Postmark-Webhook-Token"] = "wrong";
            var result = await controller.HandleEvent();

            Assert.IsType<ForbidResult>(result);
        }

        [Fact]
        public async Task RejectsWhenTokenHeaderMissing()
        {
            SetupVerifierConfigured();

            var controller = CreateController(BuildDeliveryPayload());
            // No header set
            var result = await controller.HandleEvent();

            Assert.IsType<ForbidResult>(result);
        }

        // ─── Delivery events ───

        [Fact]
        public async Task DeliveryEvent_StoresDeliveredStatus()
        {
            SetupVerifierNotConfigured(); // dev mode

            EmailDeliveryEvent? stored = null;
            _deliveryEventRepo
                .Setup(r => r.AddAsync(It.IsAny<EmailDeliveryEvent>()))
                .Callback<EmailDeliveryEvent>(e => stored = e)
                .Returns(Task.CompletedTask);

            var controller = CreateController(BuildDeliveryPayload());
            var result = await controller.HandleEvent();

            Assert.IsType<OkResult>(result);
            Assert.NotNull(stored);
            Assert.Equal(TestJobId, stored.JobId);
            Assert.Equal("user@example.com", stored.Email);
            Assert.Equal(DeliveryStatus.Delivered, stored.DeliveryStatus);
            Assert.Equal("Postmark", stored.Provider);
            Assert.Equal("Delivery", stored.ProviderEventType);
            Assert.Equal("test-msg-id", stored.ProviderMessageId);
        }

        [Fact]
        public async Task DeliveryEvent_UsesRecipientField()
        {
            SetupVerifierNotConfigured();

            EmailDeliveryEvent? stored = null;
            _deliveryEventRepo
                .Setup(r => r.AddAsync(It.IsAny<EmailDeliveryEvent>()))
                .Callback<EmailDeliveryEvent>(e => stored = e)
                .Returns(Task.CompletedTask);

            var controller = CreateController(BuildDeliveryPayload(recipient: "specific@test.com"));
            await controller.HandleEvent();

            Assert.NotNull(stored);
            Assert.Equal("specific@test.com", stored.Email);
        }

        // ─── Bounce events ───

        [Fact]
        public async Task HardBounce_MapsToBounced()
        {
            SetupVerifierNotConfigured();

            EmailDeliveryEvent? stored = null;
            _deliveryEventRepo
                .Setup(r => r.AddAsync(It.IsAny<EmailDeliveryEvent>()))
                .Callback<EmailDeliveryEvent>(e => stored = e)
                .Returns(Task.CompletedTask);

            var controller = CreateController(BuildBouncePayload("HardBounce"));
            await controller.HandleEvent();

            Assert.NotNull(stored);
            Assert.Equal(DeliveryStatus.Bounced, stored.DeliveryStatus);
            Assert.Equal("Bounce", stored.ProviderEventType);
        }

        [Fact]
        public async Task SoftBounce_MapsToDeferred()
        {
            SetupVerifierNotConfigured();

            EmailDeliveryEvent? stored = null;
            _deliveryEventRepo
                .Setup(r => r.AddAsync(It.IsAny<EmailDeliveryEvent>()))
                .Callback<EmailDeliveryEvent>(e => stored = e)
                .Returns(Task.CompletedTask);

            var controller = CreateController(BuildBouncePayload("SoftBounce"));
            await controller.HandleEvent();

            Assert.NotNull(stored);
            Assert.Equal(DeliveryStatus.Deferred, stored.DeliveryStatus);
        }

        [Fact]
        public async Task TransientBounce_MapsToDeferred()
        {
            SetupVerifierNotConfigured();

            EmailDeliveryEvent? stored = null;
            _deliveryEventRepo
                .Setup(r => r.AddAsync(It.IsAny<EmailDeliveryEvent>()))
                .Callback<EmailDeliveryEvent>(e => stored = e)
                .Returns(Task.CompletedTask);

            var controller = CreateController(BuildBouncePayload("Transient"));
            await controller.HandleEvent();

            Assert.NotNull(stored);
            Assert.Equal(DeliveryStatus.Deferred, stored.DeliveryStatus);
        }

        [Fact]
        public async Task BounceEvent_UsesEmailField()
        {
            SetupVerifierNotConfigured();

            EmailDeliveryEvent? stored = null;
            _deliveryEventRepo
                .Setup(r => r.AddAsync(It.IsAny<EmailDeliveryEvent>()))
                .Callback<EmailDeliveryEvent>(e => stored = e)
                .Returns(Task.CompletedTask);

            var controller = CreateController(BuildBouncePayload(email: "bounced@test.com"));
            await controller.HandleEvent();

            Assert.NotNull(stored);
            Assert.Equal("bounced@test.com", stored.Email);
        }

        [Fact]
        public async Task BounceEvent_DedupUsesId()
        {
            SetupVerifierNotConfigured();

            EmailDeliveryEvent? stored = null;
            _deliveryEventRepo
                .Setup(r => r.AddAsync(It.IsAny<EmailDeliveryEvent>()))
                .Callback<EmailDeliveryEvent>(e => stored = e)
                .Returns(Task.CompletedTask);

            var controller = CreateController(BuildBouncePayload(id: 12345));
            await controller.HandleEvent();

            Assert.NotNull(stored);
            Assert.Equal("bounce-12345", stored.ProviderEventId);
        }

        // ─── SpamComplaint events ───

        [Fact]
        public async Task SpamComplaint_MapsToSpamReport()
        {
            SetupVerifierNotConfigured();

            EmailDeliveryEvent? stored = null;
            _deliveryEventRepo
                .Setup(r => r.AddAsync(It.IsAny<EmailDeliveryEvent>()))
                .Callback<EmailDeliveryEvent>(e => stored = e)
                .Returns(Task.CompletedTask);

            var controller = CreateController(BuildSpamComplaintPayload());
            await controller.HandleEvent();

            Assert.NotNull(stored);
            Assert.Equal(DeliveryStatus.SpamReport, stored.DeliveryStatus);
            Assert.Equal("SpamComplaint", stored.ProviderEventType);
            Assert.Equal("complaint-99", stored.ProviderEventId);
        }

        // ─── Correlation ───

        [Fact]
        public async Task SkipsWhenMissingCohadJobId()
        {
            SetupVerifierNotConfigured();

            var payload = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["RecordType"] = "Delivery",
                ["Recipient"] = "user@example.com",
                ["MessageID"] = "test-msg-id",
                ["Metadata"] = new Dictionary<string, string>(),
            });

            var controller = CreateController(payload);
            await controller.HandleEvent();

            _deliveryEventRepo.Verify(r => r.AddAsync(It.IsAny<EmailDeliveryEvent>()), Times.Never);
        }

        [Fact]
        public async Task SkipsWhenInvalidJobId()
        {
            SetupVerifierNotConfigured();

            var payload = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["RecordType"] = "Delivery",
                ["Recipient"] = "user@example.com",
                ["MessageID"] = "test-msg-id",
                ["Metadata"] = new Dictionary<string, string>
                {
                    ["cohad_job_id"] = "not-a-guid",
                },
            });

            var controller = CreateController(payload);
            await controller.HandleEvent();

            _deliveryEventRepo.Verify(r => r.AddAsync(It.IsAny<EmailDeliveryEvent>()), Times.Never);
        }

        [Fact]
        public async Task SkipsUnknownRecordType()
        {
            SetupVerifierNotConfigured();

            var payload = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["RecordType"] = "Open",
                ["Recipient"] = "user@example.com",
                ["Metadata"] = new Dictionary<string, string>
                {
                    ["cohad_job_id"] = TestJobId.ToString(),
                },
            });

            var controller = CreateController(payload);
            await controller.HandleEvent();

            _deliveryEventRepo.Verify(r => r.AddAsync(It.IsAny<EmailDeliveryEvent>()), Times.Never);
        }

        // ─── Status mapping static method ───

        [Theory]
        [InlineData("Delivery", DeliveryStatus.Delivered)]
        [InlineData("SpamComplaint", DeliveryStatus.SpamReport)]
        public void MapRecordType_SimpleTypes(string recordType, DeliveryStatus expected)
        {
            var evt = JsonSerializer.Deserialize<JsonElement>("{}");
            Assert.Equal(expected, PostmarkWebhookController.MapRecordTypeToDeliveryStatus(recordType, evt));
        }
    }
}
