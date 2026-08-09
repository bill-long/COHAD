using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Web.MockData;
using Web.Models;
using Web.Services;
using Web.Services.Repositories;
using Xunit;

namespace Web.UnitTests
{
    /// <summary>
    /// The provider-feedback writer of the suppression list. The doc-mandated invariant lock
    /// lives here: a bounce or complaint writes a suppression and leaves every opt-in boolean
    /// untouched - the service no longer even sees the home and resident repositories.
    /// </summary>
    public class EmailDeliveryActionServiceTests
    {
        private static readonly Guid TestJobId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        private readonly MockEmailSuppressionRepository _suppressions = new();
        private readonly Mock<IAuditLogRepository> _auditLog = new();
        private readonly TestClock _time = new();

        private EmailDeliveryActionService CreateService()
        {
            return new EmailDeliveryActionService(
                new EmailSuppressionService(_suppressions, _time),
                _auditLog.Object,
                NullLogger<EmailDeliveryActionService>.Instance
            );
        }

        private static EmailDeliveryEvent NewEvent(
            string email = "bounce@example.com",
            DeliveryStatus status = DeliveryStatus.Bounced,
            string diagnostic = "HardBounce: The server was unable to deliver your message.",
            Guid? jobId = null
        )
        {
            return new EmailDeliveryEvent
            {
                Id = "evt-1",
                JobId = jobId ?? TestJobId,
                Email = email,
                DeliveryStatus = status,
                ProviderDiagnostic = diagnostic,
                Provider = "Postmark",
                ReceivedUtc = DateTime.UtcNow,
            };
        }

        // ─── The invariant lock (docs/email-suppression-and-unsubscribe.md test list) ───

        [Fact]
        public async Task Bounce_WritesASuppressionAndTouchesNoPreferences()
        {
            // The old behaviour cleared all five opt-in booleans across every home carrying the
            // address. The new contract is structural: the service has no home or resident
            // repository to write through, so the preferences CANNOT be touched - this test locks
            // the constructor shape as much as the behaviour.
            var service = CreateService();
            await service.ProcessDeliveryEventAsync(NewEvent(), "board");

            var suppression = await _suppressions.GetByEmailAsync("bounce@example.com");
            Assert.NotNull(suppression);
            Assert.True(suppression!.IsActive);
            Assert.Equal(SuppressionReason.HardBounce, suppression.Reason);
            Assert.Equal(EmailSuppression.SystemDeliveryEvent, suppression.SuppressedBy);
            Assert.Equal(TestJobId, suppression.CausingJobId);
            Assert.Equal(
                "HardBounce: The server was unable to deliver your message.",
                suppression.ProviderDiagnostic
            );
        }

        [Fact]
        public async Task SpamReport_WritesASpamComplaintSuppression()
        {
            var service = CreateService();
            await service.ProcessDeliveryEventAsync(
                NewEvent("spam@example.com", DeliveryStatus.SpamReport, "SpamComplaint: marked as spam"),
                null
            );

            var suppression = await _suppressions.GetByEmailAsync("spam@example.com");
            Assert.Equal(SuppressionReason.SpamComplaint, suppression!.Reason);
        }

        [Theory]
        [InlineData(DeliveryStatus.Delivered)]
        [InlineData(DeliveryStatus.Deferred)]
        [InlineData(DeliveryStatus.Rejected)]
        [InlineData(DeliveryStatus.Unknown)]
        public async Task NonSuppressingStatus_TakesNoAction(DeliveryStatus status)
        {
            // Deferred covers soft bounces: the webhook controllers map Postmark's
            // Transient/SoftBounce there, so the deferred-soft-bounce rule needs no code here.
            var service = CreateService();
            await service.ProcessDeliveryEventAsync(NewEvent(status: status), null);

            Assert.Empty(await _suppressions.GetAllAsync());
            _auditLog.Verify(r => r.AddAsync(It.IsAny<NewAuditLogEntry>()), Times.Never);
        }

        [Fact]
        public async Task DistinctBounceEvents_IncrementTheExistingRecord()
        {
            var service = CreateService();
            var first = NewEvent();
            first.Id = "evt-1";
            var second = NewEvent(diagnostic: "HardBounce: mailbox full");
            second.Id = "evt-2";

            await service.ProcessDeliveryEventAsync(first, null);
            await service.ProcessDeliveryEventAsync(second, null);

            var suppression = await _suppressions.GetByEmailAsync("bounce@example.com");
            Assert.Equal(2, suppression!.ConsecutiveFailureCount);
            Assert.Equal("HardBounce: mailbox full", suppression.ProviderDiagnostic);
        }

        [Fact]
        public async Task ReplayedEvent_NeitherRecountsNorReaudits()
        {
            // This method runs at-least-once per event: the caller marks the event processed only
            // AFTER it returns, and that mark can fail. The event's deduplicated id is the
            // evidence key, so a replay changes nothing - without it, one bounce reads as
            // "Evidence count 3" on the page an admin uses to decide typo-vs-dead-mailbox, and
            // the audit log gains duplicate entries for a single failure.
            var service = CreateService();
            var evt = NewEvent();

            await service.ProcessDeliveryEventAsync(evt, "board");
            await service.ProcessDeliveryEventAsync(evt, "board");
            await service.ProcessDeliveryEventAsync(evt, "board");

            var suppression = await _suppressions.GetByEmailAsync("bounce@example.com");
            Assert.Equal(1, suppression!.ConsecutiveFailureCount);
            _auditLog.Verify(r => r.AddAsync(It.IsAny<NewAuditLogEntry>()), Times.Once);
        }

        [Fact]
        public async Task EmptyJobId_StoresNullCausingJob()
        {
            // Guid.Empty is the "no job" sentinel on the event; the record stores the absence
            // honestly rather than a zero guid an admin would try to look up.
            var service = CreateService();
            await service.ProcessDeliveryEventAsync(NewEvent(jobId: Guid.Empty), null);

            var suppression = await _suppressions.GetByEmailAsync("bounce@example.com");
            Assert.Null(suppression!.CausingJobId);
        }

        // ─── Audit ───

        [Fact]
        public async Task Bounce_WritesARedactedAuditEntryNamingTheInvariant()
        {
            NewAuditLogEntry entry = null;
            _auditLog
                .Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>()))
                .Callback<NewAuditLogEntry>(e => entry = e)
                .Returns(Task.CompletedTask);

            var service = CreateService();
            await service.ProcessDeliveryEventAsync(NewEvent(), "board");

            Assert.NotNull(entry);
            Assert.Equal("system", entry.UserId);
            Assert.Equal("bou***@example.com", entry.SubjectId);
            Assert.Contains("hard bounce", entry.Action);
            Assert.Contains("category: board", entry.Action);
            Assert.Contains("preferences were not changed", entry.Action);
            Assert.DoesNotContain("bounce@example.com", entry.Action);
        }

        // ─── Email redaction ───

        [Theory]
        [InlineData("alice@example.com", "ali***@example.com")]
        [InlineData("ab@example.com", "ab***@example.com")]
        [InlineData("a@x.com", "a***@x.com")]
        [InlineData("longuser@domain.org", "lon***@domain.org")]
        public void RedactEmail_ShowsPartialLocalPart(string input, string expected)
        {
            Assert.Equal(expected, EmailDeliveryActionService.RedactEmail(input));
        }
    }
}
