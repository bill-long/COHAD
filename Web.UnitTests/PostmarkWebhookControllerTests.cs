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
        private readonly Mock<IEmailDeliveryActionService> _deliveryActionService = new();
        private readonly Mock<IWebHostEnvironment> _env = new();

        public PostmarkWebhookControllerTests()
        {
            _env.Setup(e => e.EnvironmentName).Returns("MockData");
            _deliveryEventRepo
                .Setup(r => r.AddAsync(It.IsAny<EmailDeliveryEvent>()))
                .Returns(Task.CompletedTask);
            _deliveryActionService
                .Setup(s =>
                    s.ProcessSubscriptionChangeAsync(
                        It.IsAny<string>(),
                        It.IsAny<bool>(),
                        It.IsAny<string?>(),
                        It.IsAny<string?>(),
                        It.IsAny<string?>(),
                        It.IsAny<Guid?>(),
                        It.IsAny<string>()
                    )
                )
                .ReturnsAsync(SubscriptionChangeAction.Suppressed);
        }

        private PostmarkWebhookController CreateController(string body)
        {
            var controller = new PostmarkWebhookController(
                _verifier.Object,
                _deliveryEventRepo.Object,
                _deliveryActionService.Object,
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

        // ─── Provider diagnostic ───

        [Fact]
        public async Task BounceEvent_ComposesProviderDiagnosticFromTypeAndDescription()
        {
            // The diagnostic is the provider's own text, captured at parse time - it is what the
            // suppression record shows an admin, so losing it here loses it everywhere.
            SetupVerifierNotConfigured();

            EmailDeliveryEvent? stored = null;
            _deliveryEventRepo
                .Setup(r => r.AddAsync(It.IsAny<EmailDeliveryEvent>()))
                .Callback<EmailDeliveryEvent>(e => stored = e)
                .Returns(Task.CompletedTask);

            var payload = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["RecordType"] = "Bounce",
                ["Type"] = "HardBounce",
                ["Description"] = "The server was unable to deliver your message.",
                ["Details"] = "smtp;550 5.1.1 user unknown",
                ["Email"] = "user@example.com",
                ["ID"] = 42L,
                ["Metadata"] = new Dictionary<string, string> { ["cohad_job_id"] = TestJobId.ToString() },
            });

            var controller = CreateController(payload);
            await controller.HandleEvent();

            Assert.Equal(
                "HardBounce: The server was unable to deliver your message.",
                stored!.ProviderDiagnostic
            );
        }

        [Fact]
        public async Task BounceEvent_FallsBackToDetailsWhenDescriptionIsMissing()
        {
            SetupVerifierNotConfigured();

            EmailDeliveryEvent? stored = null;
            _deliveryEventRepo
                .Setup(r => r.AddAsync(It.IsAny<EmailDeliveryEvent>()))
                .Callback<EmailDeliveryEvent>(e => stored = e)
                .Returns(Task.CompletedTask);

            var payload = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["RecordType"] = "Bounce",
                ["Type"] = "HardBounce",
                ["Details"] = "smtp;550 5.1.1 user unknown",
                ["Email"] = "user@example.com",
                ["ID"] = 42L,
                ["Metadata"] = new Dictionary<string, string> { ["cohad_job_id"] = TestJobId.ToString() },
            });

            var controller = CreateController(payload);
            await controller.HandleEvent();

            Assert.Equal("HardBounce: smtp;550 5.1.1 user unknown", stored!.ProviderDiagnostic);
        }

        [Fact]
        public async Task SpamComplaint_ComposesProviderDiagnostic()
        {
            SetupVerifierNotConfigured();

            EmailDeliveryEvent? stored = null;
            _deliveryEventRepo
                .Setup(r => r.AddAsync(It.IsAny<EmailDeliveryEvent>()))
                .Callback<EmailDeliveryEvent>(e => stored = e)
                .Returns(Task.CompletedTask);

            var payload = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["RecordType"] = "SpamComplaint",
                ["Type"] = "SpamComplaint",
                ["Description"] = "The subscriber explicitly marked this message as spam.",
                ["Email"] = "user@example.com",
                ["ID"] = 99L,
                ["Metadata"] = new Dictionary<string, string> { ["cohad_job_id"] = TestJobId.ToString() },
            });

            var controller = CreateController(payload);
            await controller.HandleEvent();

            Assert.Equal(
                "SpamComplaint: The subscriber explicitly marked this message as spam.",
                stored!.ProviderDiagnostic
            );
        }

        [Fact]
        public async Task DeliveryEvent_CarriesNoProviderDiagnostic()
        {
            // A delivery needs no explaining; a non-null value here would end up presented as the
            // "why" on a suppression record it has nothing to do with.
            SetupVerifierNotConfigured();

            EmailDeliveryEvent? stored = null;
            _deliveryEventRepo
                .Setup(r => r.AddAsync(It.IsAny<EmailDeliveryEvent>()))
                .Callback<EmailDeliveryEvent>(e => stored = e)
                .Returns(Task.CompletedTask);

            var controller = CreateController(BuildDeliveryPayload());
            await controller.HandleEvent();

            Assert.Null(stored!.ProviderDiagnostic);
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

        // ─── SubscriptionChange events ───

        private static string BuildSubscriptionChangePayload(
            string? recipient = "user@example.com",
            bool suppressSending = true,
            string origin = "Recipient",
            string? postmarkReason = "ManualSuppression",
            string stream = "broadcast",
            string? messageId = "msg-123",
            string changedAt = "2026-07-15T18:30:00Z",
            string? jobId = null,
            bool includeMessageId = true,
            bool includeMetadata = true
        )
        {
            var payload = new Dictionary<string, object>
            {
                ["RecordType"] = "SubscriptionChange",
                ["ChangedAt"] = changedAt,
                ["Origin"] = origin,
                ["SuppressSending"] = suppressSending,
                ["MessageStream"] = stream,
            };
            if (recipient != null)
                payload["Recipient"] = recipient;
            if (postmarkReason != null)
                payload["SuppressionReason"] = postmarkReason;
            if (includeMessageId && messageId != null)
                payload["MessageID"] = messageId;
            if (includeMetadata)
            {
                var metadata = new Dictionary<string, string>();
                if (jobId != null)
                    metadata["cohad_job_id"] = jobId;
                payload["Metadata"] = metadata;
            }
            return JsonSerializer.Serialize(payload);
        }

        [Fact]
        public async Task SubscriptionChange_Unsubscribe_RecordsAndStoresJobCorrelatedEvent()
        {
            SetupVerifierNotConfigured();

            EmailDeliveryEvent? stored = null;
            _deliveryEventRepo
                .Setup(r => r.AddAsync(It.IsAny<EmailDeliveryEvent>()))
                .Callback<EmailDeliveryEvent>(e => stored = e)
                .Returns(Task.CompletedTask);

            var controller = CreateController(
                BuildSubscriptionChangePayload(jobId: TestJobId.ToString())
            );
            var result = await controller.HandleEvent();

            Assert.IsType<OkResult>(result);
            _deliveryActionService.Verify(
                s =>
                    s.ProcessSubscriptionChangeAsync(
                        "user@example.com",
                        true,
                        "Recipient",
                        "ManualSuppression",
                        "broadcast",
                        TestJobId,
                        "postmark:subscription-change:msg-123"
                    ),
                Times.Once
            );
            Assert.NotNull(stored);
            Assert.Equal(TestJobId, stored!.JobId);
            Assert.Equal("user@example.com", stored.Email);
            // Unknown never overrides a truthful Delivered on the recipient row - the message
            // WAS delivered; the unsubscribe happened afterwards at the provider layer.
            Assert.Equal(DeliveryStatus.Unknown, stored.DeliveryStatus);
            Assert.Equal("SubscriptionChange", stored.ProviderEventType);
            Assert.Equal("msg-123", stored.ProviderMessageId);
            Assert.True(stored.ActionProcessed);
            Assert.Contains("broadcast", stored.ProviderDiagnostic);
        }

        [Fact]
        public async Task SubscriptionChange_WithoutJobCorrelation_RecordsWithoutStoringEvent()
        {
            // Manual suppressions (Origin Admin/Customer) carry no MessageID and empty Metadata.
            // The suppression must still be recorded - the per-job event sweep would never see a
            // jobless event, so correlation is a bonus, not a requirement.
            SetupVerifierNotConfigured();

            var controller = CreateController(
                BuildSubscriptionChangePayload(
                    origin: "Admin",
                    includeMessageId: false,
                    includeMetadata: false
                )
            );
            var result = await controller.HandleEvent();

            Assert.IsType<OkResult>(result);
            _deliveryActionService.Verify(
                s =>
                    s.ProcessSubscriptionChangeAsync(
                        "user@example.com",
                        true,
                        "Admin",
                        "ManualSuppression",
                        "broadcast",
                        null,
                        "postmark:subscription-change:2026-07-15T18:30:00Z"
                    ),
                Times.Once
            );
            _deliveryEventRepo.Verify(r => r.AddAsync(It.IsAny<EmailDeliveryEvent>()), Times.Never);
        }

        [Fact]
        public async Task SubscriptionChange_BounceOrigin_StoresNoEvent()
        {
            // The service skips bounce/complaint-origin changes (the Bounce webhook owns them);
            // the controller must then not store a SubscriptionChange event either - the skip
            // rule lives in exactly one place.
            SetupVerifierNotConfigured();
            _deliveryActionService
                .Setup(s =>
                    s.ProcessSubscriptionChangeAsync(
                        It.IsAny<string>(),
                        It.IsAny<bool>(),
                        It.IsAny<string?>(),
                        It.IsAny<string?>(),
                        It.IsAny<string?>(),
                        It.IsAny<Guid?>(),
                        It.IsAny<string>()
                    )
                )
                .ReturnsAsync(SubscriptionChangeAction.None);

            var controller = CreateController(
                BuildSubscriptionChangePayload(postmarkReason: "HardBounce", jobId: TestJobId.ToString())
            );
            await controller.HandleEvent();

            _deliveryActionService.Verify(
                s =>
                    s.ProcessSubscriptionChangeAsync(
                        "user@example.com",
                        true,
                        "Recipient",
                        "HardBounce",
                        "broadcast",
                        TestJobId,
                        It.IsAny<string>()
                    ),
                Times.Once
            );
            _deliveryEventRepo.Verify(r => r.AddAsync(It.IsAny<EmailDeliveryEvent>()), Times.Never);
        }

        [Fact]
        public async Task SubscriptionChange_Reactivation_ClearsAndStoresNoEvent()
        {
            SetupVerifierNotConfigured();
            _deliveryActionService
                .Setup(s =>
                    s.ProcessSubscriptionChangeAsync(
                        It.IsAny<string>(),
                        It.IsAny<bool>(),
                        It.IsAny<string?>(),
                        It.IsAny<string?>(),
                        It.IsAny<string?>(),
                        It.IsAny<Guid?>(),
                        It.IsAny<string>()
                    )
                )
                .ReturnsAsync(SubscriptionChangeAction.Cleared);

            var controller = CreateController(
                BuildSubscriptionChangePayload(
                    suppressSending: false,
                    postmarkReason: null,
                    includeMessageId: false
                )
            );
            var result = await controller.HandleEvent();

            Assert.IsType<OkResult>(result);
            _deliveryActionService.Verify(
                s =>
                    s.ProcessSubscriptionChangeAsync(
                        "user@example.com",
                        false,
                        "Recipient",
                        null,
                        "broadcast",
                        null,
                        "postmark:subscription-change:2026-07-15T18:30:00Z"
                    ),
                Times.Once
            );
            _deliveryEventRepo.Verify(r => r.AddAsync(It.IsAny<EmailDeliveryEvent>()), Times.Never);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("   ")]
        public async Task SubscriptionChange_MissingRecipient_IsANoOp(string? recipient)
        {
            SetupVerifierNotConfigured();

            var controller = CreateController(BuildSubscriptionChangePayload(recipient: recipient));
            var result = await controller.HandleEvent();

            Assert.IsType<OkResult>(result);
            _deliveryActionService.Verify(
                s =>
                    s.ProcessSubscriptionChangeAsync(
                        It.IsAny<string>(),
                        It.IsAny<bool>(),
                        It.IsAny<string?>(),
                        It.IsAny<string?>(),
                        It.IsAny<string?>(),
                        It.IsAny<Guid?>(),
                        It.IsAny<string>()
                    ),
                Times.Never
            );
        }

        [Fact]
        public async Task SubscriptionChange_InvalidJobIdMetadata_RecordsJoblessOffMessageId()
        {
            // An unparsable cohad_job_id is an absence, not a rejection: the suppression records
            // without job correlation and the evidence key falls back to the MessageID.
            SetupVerifierNotConfigured();

            var payload = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["RecordType"] = "SubscriptionChange",
                ["ChangedAt"] = "2026-07-15T18:30:00Z",
                ["Recipient"] = "user@example.com",
                ["Origin"] = "Recipient",
                ["SuppressSending"] = true,
                ["SuppressionReason"] = "ManualSuppression",
                ["MessageStream"] = "broadcast",
                ["MessageID"] = "msg-123",
                ["Metadata"] = new Dictionary<string, string> { ["cohad_job_id"] = "not-a-guid" },
            });

            var controller = CreateController(payload);
            var result = await controller.HandleEvent();

            Assert.IsType<OkResult>(result);
            _deliveryActionService.Verify(
                s =>
                    s.ProcessSubscriptionChangeAsync(
                        "user@example.com",
                        true,
                        "Recipient",
                        "ManualSuppression",
                        "broadcast",
                        null,
                        "postmark:subscription-change:msg-123"
                    ),
                Times.Once
            );
            _deliveryEventRepo.Verify(r => r.AddAsync(It.IsAny<EmailDeliveryEvent>()), Times.Never);
        }

        [Fact]
        public async Task SubscriptionChange_MissingSuppressSending_IsANoOp()
        {
            SetupVerifierNotConfigured();

            var payload = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["RecordType"] = "SubscriptionChange",
                ["Recipient"] = "user@example.com",
                ["Origin"] = "Recipient",
            });

            var controller = CreateController(payload);
            var result = await controller.HandleEvent();

            Assert.IsType<OkResult>(result);
            _deliveryActionService.Verify(
                s =>
                    s.ProcessSubscriptionChangeAsync(
                        It.IsAny<string>(),
                        It.IsAny<bool>(),
                        It.IsAny<string?>(),
                        It.IsAny<string?>(),
                        It.IsAny<string?>(),
                        It.IsAny<Guid?>(),
                        It.IsAny<string>()
                    ),
                Times.Never
            );
        }

        [Fact]
        public async Task SubscriptionChange_NonStringFields_ReadAsAbsentNotException()
        {
            // The payload is drift- and attacker-exposed: non-string values must read as absent
            // (a 200 no-op or a record with "unknown" provenance), never a 500-and-retry loop.
            SetupVerifierNotConfigured();

            var payload = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["RecordType"] = "SubscriptionChange",
                ["ChangedAt"] = "2026-07-15T18:30:00Z",
                ["Recipient"] = "user@example.com",
                ["Origin"] = 123,
                ["SuppressSending"] = true,
                ["SuppressionReason"] = 42,
                ["MessageStream"] = true,
                ["MessageID"] = 12345,
            });

            var controller = CreateController(payload);
            var result = await controller.HandleEvent();

            Assert.IsType<OkResult>(result);
            _deliveryActionService.Verify(
                s =>
                    s.ProcessSubscriptionChangeAsync(
                        "user@example.com",
                        true,
                        null,
                        null,
                        null,
                        null,
                        "postmark:subscription-change:2026-07-15T18:30:00Z"
                    ),
                Times.Once
            );
        }

        [Fact]
        public async Task SubscriptionChange_BlankMessageIdAndChangedAt_FallsBackToPayloadHash()
        {
            // A blank string is absent: the evidence key must not collapse to an empty suffix,
            // which would falsely dedup two distinct blank-keyed changes into one.
            SetupVerifierNotConfigured();

            string? capturedKey = null;
            _deliveryActionService
                .Setup(s =>
                    s.ProcessSubscriptionChangeAsync(
                        It.IsAny<string>(),
                        It.IsAny<bool>(),
                        It.IsAny<string?>(),
                        It.IsAny<string?>(),
                        It.IsAny<string?>(),
                        It.IsAny<Guid?>(),
                        It.IsAny<string>()
                    )
                )
                .Callback<string, bool, string?, string?, string?, Guid?, string>(
                    (_, _, _, _, _, _, key) => capturedKey = key
                )
                .ReturnsAsync(SubscriptionChangeAction.Suppressed);

            var payload =
                "{\"RecordType\":\"SubscriptionChange\",\"Recipient\":\"user@example.com\","
                + "\"Origin\":\"Recipient\",\"SuppressSending\":true,\"SuppressionReason\":\"ManualSuppression\","
                + "\"MessageStream\":\"broadcast\",\"MessageID\":\"  \",\"ChangedAt\":\"\"}";

            var controller = CreateController(payload);
            var result = await controller.HandleEvent();

            Assert.IsType<OkResult>(result);
            Assert.NotNull(capturedKey);
            Assert.Matches(@"^postmark:subscription-change:[0-9a-f]{64}$", capturedKey);
        }

        [Fact]
        public async Task DeliveryEvent_NonStringRecipient_IsANoOp()
        {
            // Same lenient-parse rule on the pre-existing path: a malformed payload gets a 200
            // no-op rather than an unending 500-and-retry loop.
            SetupVerifierNotConfigured();

            var payload = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["RecordType"] = "Delivery",
                ["Recipient"] = 12345,
                ["MessageID"] = "test-msg-id",
                ["Metadata"] = new Dictionary<string, string>
                {
                    ["cohad_job_id"] = TestJobId.ToString(),
                },
            });

            var controller = CreateController(payload);
            var result = await controller.HandleEvent();

            Assert.IsType<OkResult>(result);
            _deliveryEventRepo.Verify(r => r.AddAsync(It.IsAny<EmailDeliveryEvent>()), Times.Never);
        }

        [Fact]
        public async Task NonStringRecordType_IsANoOp()
        {
            // The discriminator itself is parsed leniently too: a non-string RecordType must not
            // throw into the 500-and-retry loop.
            SetupVerifierNotConfigured();

            var controller = CreateController("{\"RecordType\":123,\"Recipient\":\"user@example.com\"}");
            var result = await controller.HandleEvent();

            Assert.IsType<OkResult>(result);
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
