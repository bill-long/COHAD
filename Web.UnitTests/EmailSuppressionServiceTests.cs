using System;
using System.Threading.Tasks;
using Moq;
using Web.MockData;
using Web.Models;
using Web.Services;
using Web.Services.Repositories;

namespace Web.UnitTests
{
    /// <summary>
    /// The suppression record lifecycle: first evidence, repeat evidence, clear, and
    /// re-suppression, plus the lost-race retry. One document per address; the rules live in
    /// <see cref="EmailSuppressionService"/> and nowhere else.
    /// </summary>
    public class EmailSuppressionServiceTests
    {
        private static readonly DateTimeOffset Now = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);

        private readonly MockEmailSuppressionRepository _repo = new();
        private readonly TestClock _time = new(Now);

        private EmailSuppressionService CreateService() => new(_repo, _time);

        [Fact]
        public async Task FirstEvidence_CreatesTheRecordWithAllStampsAtNow()
        {
            var service = CreateService();
            var jobId = Guid.NewGuid();

            var result = await service.RecordAsync(
                "Jane@Example.com ",
                SuppressionReason.HardBounce,
                EmailSuppression.SystemDeliveryEvent,
                jobId,
                "HardBounce: mailbox does not exist"
            );

            Assert.Equal("jane@example.com", result.Email);
            Assert.Equal(EmailSuppression.MakeId("jane@example.com"), result.Id);
            Assert.Equal(SuppressionReason.HardBounce, result.Reason);
            Assert.Equal(1, result.ConsecutiveFailureCount);
            Assert.Equal(Now.UtcDateTime, result.FirstSeenUtc);
            Assert.Equal(Now.UtcDateTime, result.LastSeenUtc);
            Assert.Equal(Now.UtcDateTime, result.SuppressedUtc);
            Assert.Equal(jobId, result.CausingJobId);
            Assert.Equal(EmailSuppression.SystemDeliveryEvent, result.SuppressedBy);
            Assert.Equal("HardBounce: mailbox does not exist", result.ProviderDiagnostic);
            Assert.True(result.IsActive);

            Assert.NotNull(await _repo.GetByEmailAsync("jane@example.com"));
        }

        [Fact]
        public async Task RepeatEvidence_AdvancesCountersButKeepsTheOriginalWhy()
        {
            // "Why is this address suppressed" is still the first event; later evidence only
            // proves the suppression is still warranted.
            var service = CreateService();
            var firstJob = Guid.NewGuid();
            await service.RecordAsync(
                "jane@example.com",
                SuppressionReason.SpamComplaint,
                EmailSuppression.SystemDeliveryEvent,
                firstJob,
                "SpamComplaint: marked as spam"
            );

            _time.Advance(TimeSpan.FromDays(2));
            var secondJob = Guid.NewGuid();
            var result = await service.RecordAsync(
                "jane@example.com",
                SuppressionReason.HardBounce,
                "ShortLink",
                secondJob,
                "HardBounce: mailbox full"
            );

            Assert.Equal(2, result.ConsecutiveFailureCount);
            Assert.Equal(Now.UtcDateTime, result.FirstSeenUtc);
            Assert.Equal(Now.UtcDateTime.AddDays(2), result.LastSeenUtc);
            // The original why survives...
            Assert.Equal(SuppressionReason.SpamComplaint, result.Reason);
            Assert.Equal(EmailSuppression.SystemDeliveryEvent, result.SuppressedBy);
            Assert.Equal(Now.UtcDateTime, result.SuppressedUtc);
            // ...while the freshest provider evidence wins.
            Assert.Equal("HardBounce: mailbox full", result.ProviderDiagnostic);
            Assert.Equal(secondJob, result.CausingJobId);
        }

        [Fact]
        public async Task RepeatEvidence_WithoutADiagnosticKeepsTheStoredOne()
        {
            // A one-click arriving after a bounce carries no provider text; blanking the stored
            // diagnostic would delete the very evidence the record exists to preserve.
            var service = CreateService();
            await service.RecordAsync(
                "jane@example.com",
                SuppressionReason.HardBounce,
                EmailSuppression.SystemDeliveryEvent,
                Guid.NewGuid(),
                "HardBounce: mailbox does not exist"
            );

            var result = await service.RecordAsync(
                "jane@example.com",
                SuppressionReason.ResidentRequest,
                "ShortLink",
                null,
                null
            );

            Assert.Equal("HardBounce: mailbox does not exist", result.ProviderDiagnostic);
        }

        [Fact]
        public async Task Clear_StampsClearedFieldsAndDeactivates()
        {
            var service = CreateService();
            await service.RecordAsync(
                "jane@example.com",
                SuppressionReason.HardBounce,
                EmailSuppression.SystemDeliveryEvent,
                null,
                null
            );

            _time.Advance(TimeSpan.FromHours(1));
            var cleared = await service.ClearAsync("JANE@example.com", "admin-user");

            Assert.NotNull(cleared);
            Assert.False(cleared!.IsActive);
            Assert.Equal(Now.UtcDateTime.AddHours(1), cleared.ClearedUtc);
            Assert.Equal("admin-user", cleared.ClearedBy);
            // The suppression's own history is untouched by the clear.
            Assert.Equal(1, cleared.ConsecutiveFailureCount);
            Assert.Equal(SuppressionReason.HardBounce, cleared.Reason);
        }

        [Fact]
        public async Task Clear_IsIdempotentAndKeepsTheOriginalClearStamps()
        {
            var service = CreateService();
            await service.RecordAsync(
                "jane@example.com",
                SuppressionReason.ResidentRequest,
                "ShortLink",
                null,
                null
            );

            _time.Advance(TimeSpan.FromHours(1));
            await service.ClearAsync("jane@example.com", "first-admin");

            _time.Advance(TimeSpan.FromHours(1));
            var second = await service.ClearAsync("jane@example.com", "second-admin");

            // The original clear's stamps are the truthful ones - the second call found nothing
            // to do and must not rewrite who did the clearing.
            Assert.Equal(Now.UtcDateTime.AddHours(1), second!.ClearedUtc);
            Assert.Equal("first-admin", second.ClearedBy);
        }

        [Fact]
        public async Task Clear_ReturnsNullWhenNoRecordExists()
        {
            var service = CreateService();

            Assert.Null(await service.ClearAsync("nobody@example.com", "admin-user"));
        }

        [Fact]
        public async Task ResuppressionAfterClear_StartsANewEpisodeButKeepsFirstSeen()
        {
            var service = CreateService();
            var firstJob = Guid.NewGuid();
            await service.RecordAsync(
                "jane@example.com",
                SuppressionReason.HardBounce,
                EmailSuppression.SystemDeliveryEvent,
                firstJob,
                "HardBounce: mailbox does not exist"
            );
            _time.Advance(TimeSpan.FromDays(1));
            await service.ClearAsync("jane@example.com", "admin-user");

            _time.Advance(TimeSpan.FromDays(1));
            var result = await service.RecordAsync(
                "jane@example.com",
                SuppressionReason.ResidentRequest,
                "ShortLink",
                null,
                null
            );

            // A new episode: the why fields describe what put the suppression back in force...
            Assert.True(result.IsActive);
            Assert.Equal(SuppressionReason.ResidentRequest, result.Reason);
            Assert.Equal("ShortLink", result.SuppressedBy);
            Assert.Equal(Now.UtcDateTime.AddDays(2), result.SuppressedUtc);
            Assert.Null(result.ProviderDiagnostic);
            Assert.Null(result.CausingJobId);
            Assert.Null(result.ClearedUtc);
            Assert.Null(result.ClearedBy);
            // ...while the record still reads as a history.
            Assert.Equal(Now.UtcDateTime, result.FirstSeenUtc);
            Assert.Equal(2, result.ConsecutiveFailureCount);
        }

        [Fact]
        public async Task Record_RetriesIntoRepeatEvidenceWhenTheCreateRaceIsLost()
        {
            // Two writers racing on one address converge on the same document: the loser's Add
            // throws ConcurrencyConflictException, and the retry re-reads the winner's record and
            // applies this evidence on top of it.
            var repo = new Mock<IEmailSuppressionRepository>();
            var stored = new EmailSuppression
            {
                Id = EmailSuppression.MakeId("jane@example.com"),
                Email = "jane@example.com",
                Reason = SuppressionReason.HardBounce,
                ConsecutiveFailureCount = 1,
                FirstSeenUtc = Now.UtcDateTime.AddMinutes(-1),
                LastSeenUtc = Now.UtcDateTime.AddMinutes(-1),
                SuppressedUtc = Now.UtcDateTime.AddMinutes(-1),
                SuppressedBy = EmailSuppression.SystemDeliveryEvent,
                ETag = "1",
            };

            // First read: no record (so the service tries Add). Second read: the winner's record.
            repo.SetupSequence(r => r.GetByEmailAsync("jane@example.com"))
                .ReturnsAsync((EmailSuppression)null)
                .ReturnsAsync(stored);
            repo.Setup(r => r.AddAsync(It.IsAny<EmailSuppression>()))
                .ThrowsAsync(new ConcurrencyConflictException("exists", new InvalidOperationException()));
            repo.Setup(r => r.UpdateAsync(It.IsAny<EmailSuppression>())).Returns(Task.CompletedTask);

            var service = new EmailSuppressionService(repo.Object, _time);
            var result = await service.RecordAsync(
                "jane@example.com",
                SuppressionReason.ResidentRequest,
                "ShortLink",
                null,
                null
            );

            Assert.Equal(2, result.ConsecutiveFailureCount);
            repo.Verify(r => r.UpdateAsync(It.IsAny<EmailSuppression>()), Times.Once);
        }

        [Fact]
        public async Task Record_GivesUpAfterRepeatedLostRaces()
        {
            // The bound exists so a pathological interleaving surfaces as a failure rather than a
            // loop; the last conflict rides along as the inner exception.
            var repo = new Mock<IEmailSuppressionRepository>();
            repo.Setup(r => r.GetByEmailAsync(It.IsAny<string>())).ReturnsAsync((EmailSuppression)null);
            repo.Setup(r => r.AddAsync(It.IsAny<EmailSuppression>()))
                .ThrowsAsync(new ConcurrencyConflictException("exists", new InvalidOperationException()));

            var service = new EmailSuppressionService(repo.Object, _time);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () =>
                    service.RecordAsync(
                        "jane@example.com",
                        SuppressionReason.HardBounce,
                        EmailSuppression.SystemDeliveryEvent,
                        null,
                        null
                    )
            );
            Assert.IsType<ConcurrencyConflictException>(ex.InnerException);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task Record_RejectsABlankAddress(string blank)
        {
            // A suppression for a blank address is not a safety measure - it is a corrupt row
            // whose only future is confusing the admin list.
            var service = CreateService();

            await Assert.ThrowsAsync<ArgumentException>(
                () =>
                    service.RecordAsync(
                        blank,
                        SuppressionReason.AdminAction,
                        "admin-user",
                        null,
                        null
                    )
            );
        }

        [Fact]
        public async Task Record_RejectsABlankSuppressedBy()
        {
            // SuppressedBy is the record's provenance; the doc's whole point is that the record
            // explains itself on screen, which a blank actor cannot.
            var service = CreateService();

            await Assert.ThrowsAsync<ArgumentException>(
                () =>
                    service.RecordAsync(
                        "jane@example.com",
                        SuppressionReason.AdminAction,
                        " ",
                        null,
                        null
                    )
            );
        }
    }
}
