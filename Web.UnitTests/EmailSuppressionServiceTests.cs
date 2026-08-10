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
    /// The suppression record lifecycle: first evidence, repeat evidence, replayed evidence,
    /// clear, and re-suppression, plus the lost-race retry. One document per address; the rules
    /// live in <see cref="EmailSuppressionService"/> and nowhere else.
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

            var outcome = await service.RecordAsync(
                "Jane@Example.com ",
                SuppressionReason.HardBounce,
                EmailSuppression.SystemDeliveryEvent,
                jobId,
                "HardBounce: mailbox does not exist",
                evidenceKey: "evt-1"
            );

            Assert.True(outcome.Applied);
            var result = outcome.Suppression;
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
            Assert.Equal("evt-1", result.LastEvidenceKey);
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
                "SpamComplaint: marked as spam",
                evidenceKey: "evt-1"
            );

            _time.Advance(TimeSpan.FromDays(2));
            var secondJob = Guid.NewGuid();
            var outcome = await service.RecordAsync(
                "jane@example.com",
                SuppressionReason.HardBounce,
                "ShortLink",
                secondJob,
                "HardBounce: mailbox full",
                evidenceKey: "evt-2"
            );

            Assert.True(outcome.Applied);
            var result = outcome.Suppression;
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
        public async Task ReplayedEvidence_ChangesNothingAndReportsNotApplied()
        {
            // The delivery-event path is at-least-once: the event is marked processed only AFTER
            // the suppression write, so a transient failure there replays the same event on the
            // next run. Counting the replay would turn one bounce into "Evidence count 3" on the
            // page an admin reads to decide typo-vs-dead-mailbox.
            var service = CreateService();
            await service.RecordAsync(
                "jane@example.com",
                SuppressionReason.HardBounce,
                EmailSuppression.SystemDeliveryEvent,
                null,
                "HardBounce: mailbox does not exist",
                evidenceKey: "evt-1"
            );

            _time.Advance(TimeSpan.FromMinutes(5));
            var replay = await service.RecordAsync(
                "jane@example.com",
                SuppressionReason.HardBounce,
                EmailSuppression.SystemDeliveryEvent,
                null,
                "HardBounce: mailbox does not exist",
                evidenceKey: "evt-1"
            );

            Assert.False(replay.Applied);
            Assert.Equal(1, replay.Suppression.ConsecutiveFailureCount);
            Assert.Equal(Now.UtcDateTime, replay.Suppression.LastSeenUtc);

            // A DIFFERENT event for the same address still counts.
            var fresh = await service.RecordAsync(
                "jane@example.com",
                SuppressionReason.HardBounce,
                EmailSuppression.SystemDeliveryEvent,
                null,
                null,
                evidenceKey: "evt-2"
            );
            Assert.True(fresh.Applied);
            Assert.Equal(2, fresh.Suppression.ConsecutiveFailureCount);
        }

        [Fact]
        public async Task KeylessEvidence_AlwaysCounts()
        {
            // One-click and admin action carry no natural dedup key and are not replayed
            // mechanically; two clicks are genuinely two pieces of evidence.
            var service = CreateService();
            await service.RecordAsync("jane@example.com", SuppressionReason.ResidentRequest, "ShortLink", null, null);
            var second = await service.RecordAsync(
                "jane@example.com",
                SuppressionReason.ResidentRequest,
                "ShortLink",
                null,
                null
            );

            Assert.True(second.Applied);
            Assert.Equal(2, second.Suppression.ConsecutiveFailureCount);
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
                "HardBounce: mailbox does not exist",
                evidenceKey: "evt-1"
            );

            var outcome = await service.RecordAsync(
                "jane@example.com",
                SuppressionReason.ResidentRequest,
                "ShortLink",
                null,
                null
            );

            Assert.Equal("HardBounce: mailbox does not exist", outcome.Suppression.ProviderDiagnostic);
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
            var outcome = await service.ClearAsync("JANE@example.com", "admin-user");

            Assert.True(outcome.Cleared);
            var cleared = outcome.Suppression;
            Assert.NotNull(cleared);
            Assert.False(cleared!.IsActive);
            Assert.Equal(Now.UtcDateTime.AddHours(1), cleared.ClearedUtc);
            Assert.Equal("admin-user", cleared.ClearedBy);
            // The suppression's own history is untouched by the clear.
            Assert.Equal(1, cleared.ConsecutiveFailureCount);
            Assert.Equal(SuppressionReason.HardBounce, cleared.Reason);
        }

        [Fact]
        public async Task Clear_IsIdempotentAndReportsTheNoOp()
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
            var first = await service.ClearAsync("jane@example.com", "first-admin");
            Assert.True(first.Cleared);

            _time.Advance(TimeSpan.FromHours(1));
            var second = await service.ClearAsync("jane@example.com", "second-admin");

            // The original clear's stamps are the truthful ones - the second call found nothing
            // to do, must not rewrite who did the clearing, and must SAY it did nothing so the
            // caller's audit entry cannot attribute an action nobody took.
            Assert.False(second.Cleared);
            Assert.Equal(Now.UtcDateTime.AddHours(1), second.Suppression!.ClearedUtc);
            Assert.Equal("first-admin", second.Suppression.ClearedBy);
        }

        [Fact]
        public async Task Clear_ReturnsNoRecordWhenNoneExists()
        {
            var service = CreateService();

            var outcome = await service.ClearAsync("nobody@example.com", "admin-user");

            Assert.Null(outcome.Suppression);
            Assert.False(outcome.Cleared);
        }

        [Fact]
        public async Task Clear_WithOnlyIfReason_RefusesADifferentReasonAtWriteTime()
        {
            // The guard lives inside the write cycle, not on a caller's earlier read - between
            // that read and this write the record can be cleared and re-suppressed for a reason
            // the caller is not allowed to lift.
            var service = CreateService();
            await service.RecordAsync(
                "jane@example.com",
                SuppressionReason.HardBounce,
                EmailSuppression.SystemDeliveryEvent,
                null,
                null
            );

            var outcome = await service.ClearAsync(
                "jane@example.com",
                "resident:ShortLink",
                onlyIfReason: SuppressionReason.ResidentRequest
            );

            Assert.False(outcome.Cleared);
            Assert.NotNull(outcome.Suppression);
            Assert.True(outcome.Suppression!.IsActive);
            Assert.True((await _repo.GetByEmailAsync("jane@example.com"))!.IsActive);
        }

        [Fact]
        public async Task Clear_WithOnlyIfReason_ClearsAMatchingReason()
        {
            var service = CreateService();
            await service.RecordAsync(
                "jane@example.com",
                SuppressionReason.ResidentRequest,
                "ShortLink",
                null,
                null
            );

            var outcome = await service.ClearAsync(
                "jane@example.com",
                "resident:ShortLink",
                onlyIfReason: SuppressionReason.ResidentRequest
            );

            Assert.True(outcome.Cleared);
            Assert.False(outcome.Suppression!.IsActive);
        }

        [Fact]
        public async Task ClearById_ClearsARecordWhoseIdDoesNotMatchItsOwnAddress()
        {
            // A hand-authored document (arbitrary id, or an Email edited in the portal) is
            // unreachable through the address-keyed path - MakeId of its Email points elsewhere -
            // but the admin surface lists it and the human looking at it must be able to clear it.
            var handAuthored = new EmailSuppression
            {
                Id = "hand-authored-row",
                Email = "jane@example.com",
                Reason = SuppressionReason.AdminAction,
                ConsecutiveFailureCount = 1,
                FirstSeenUtc = Now.UtcDateTime,
                LastSeenUtc = Now.UtcDateTime,
                SuppressedUtc = Now.UtcDateTime,
                SuppressedBy = "someone",
            };
            await _repo.AddAsync(handAuthored);

            var service = CreateService();
            var outcome = await service.ClearByIdAsync("hand-authored-row", "admin-user");

            Assert.True(outcome.Cleared);
            Assert.Equal("admin-user", outcome.Suppression!.ClearedBy);
            Assert.False((await _repo.GetByIdAsync("hand-authored-row"))!.IsActive);
        }

        [Fact]
        public async Task ClearById_ReturnsNoRecordForAnUnknownId()
        {
            var service = CreateService();

            var outcome = await service.ClearByIdAsync("no-such-id", "admin-user");

            Assert.Null(outcome.Suppression);
            Assert.False(outcome.Cleared);
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
                "HardBounce: mailbox does not exist",
                evidenceKey: "evt-1"
            );
            _time.Advance(TimeSpan.FromDays(1));
            await service.ClearAsync("jane@example.com", "admin-user");

            _time.Advance(TimeSpan.FromDays(1));
            var outcome = await service.RecordAsync(
                "jane@example.com",
                SuppressionReason.ResidentRequest,
                "ShortLink",
                null,
                null
            );

            // A new episode: the why fields describe what put the suppression back in force...
            Assert.True(outcome.Applied);
            var result = outcome.Suppression;
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
        public async Task ProviderTimestamp_BecomesSuppressedUtcOnCreation_ButSeenStampsStayNow()
        {
            // The dump reconciler passes Postmark's CreatedAt: the address stopped receiving
            // mail THEN, so that is "when the suppression began" - while first/last-seen stay
            // honest about when the evidence arrived HERE.
            var service = CreateService();
            var providerDate = Now.UtcDateTime.AddDays(-30);

            var outcome = await service.RecordAsync(
                "jane@example.com",
                SuppressionReason.ProviderUnsubscribe,
                EmailSuppression.PostmarkSuppressionDump,
                null,
                null,
                evidenceKey: "dump-1",
                eventUtc: providerDate
            );

            Assert.True(outcome.Applied);
            var result = outcome.Suppression;
            Assert.Equal(providerDate, result.SuppressedUtc);
            Assert.Equal(Now.UtcDateTime, result.FirstSeenUtc);
            Assert.Equal(Now.UtcDateTime, result.LastSeenUtc);
        }

        [Fact]
        public async Task ProviderTimestamp_InTheFuture_FallsBackToNow()
        {
            // Provider clock skew must not order the record oddly on the admin list.
            var service = CreateService();

            var outcome = await service.RecordAsync(
                "jane@example.com",
                SuppressionReason.ProviderUnsubscribe,
                EmailSuppression.PostmarkSuppressionDump,
                null,
                null,
                eventUtc: Now.UtcDateTime.AddDays(30)
            );

            Assert.Equal(Now.UtcDateTime, outcome.Suppression.SuppressedUtc);
        }

        [Fact]
        public async Task ProviderTimestamp_OnRepeatEvidence_DoesNotMoveSuppressedUtc()
        {
            // Only a NEW episode adopts the provider date. Repeat evidence on an active record
            // leaves "when it began" alone - the answer is still the first event.
            var service = CreateService();
            await service.RecordAsync(
                "jane@example.com",
                SuppressionReason.ProviderUnsubscribe,
                EmailSuppression.PostmarkSuppressionDump,
                null,
                null,
                evidenceKey: "dump-1",
                eventUtc: Now.UtcDateTime.AddDays(-30)
            );

            _time.Advance(TimeSpan.FromDays(1));
            var outcome = await service.RecordAsync(
                "jane@example.com",
                SuppressionReason.ProviderUnsubscribe,
                EmailSuppression.PostmarkSuppressionDump,
                null,
                null,
                evidenceKey: "dump-2",
                eventUtc: Now.UtcDateTime
            );

            Assert.True(outcome.Applied);
            Assert.Equal(Now.UtcDateTime.AddDays(-30), outcome.Suppression.SuppressedUtc);
            Assert.Equal(Now.UtcDateTime.AddDays(1), outcome.Suppression.LastSeenUtc);
        }

        [Fact]
        public async Task ProviderTimestamp_OnResuppression_StampsTheNewEpisode()
        {
            var service = CreateService();
            await service.RecordAsync(
                "jane@example.com",
                SuppressionReason.ProviderUnsubscribe,
                EmailSuppression.PostmarkSuppressionDump,
                null,
                null,
                eventUtc: Now.UtcDateTime.AddDays(-30)
            );
            await service.ClearAsync("jane@example.com", "admin-user");

            _time.Advance(TimeSpan.FromDays(1));
            var outcome = await service.RecordAsync(
                "jane@example.com",
                SuppressionReason.ProviderUnsubscribe,
                EmailSuppression.PostmarkSuppressionDump,
                null,
                null,
                evidenceKey: "dump-2",
                eventUtc: Now.UtcDateTime.AddDays(-30)
            );

            Assert.True(outcome.Applied);
            Assert.True(outcome.Suppression.IsActive);
            Assert.Equal(Now.UtcDateTime.AddDays(-30), outcome.Suppression.SuppressedUtc);
            // First-seen still survives as the record's history.
            Assert.Equal(Now.UtcDateTime, outcome.Suppression.FirstSeenUtc);
        }

        [Fact]
        public async Task ReplayedEvidence_AfterAClear_StillResuppresses()
        {
            // The evidence-key no-op applies only while the record is ACTIVE. A cleared record
            // seeing its own last event again means the clear raced the replay - and a replay of
            // real bounce evidence should still stop the mail; idempotence must not become a
            // loophole that leaves the address deliverable.
            var service = CreateService();
            await service.RecordAsync(
                "jane@example.com",
                SuppressionReason.HardBounce,
                EmailSuppression.SystemDeliveryEvent,
                null,
                null,
                evidenceKey: "evt-1"
            );
            await service.ClearAsync("jane@example.com", "admin-user");

            var replay = await service.RecordAsync(
                "jane@example.com",
                SuppressionReason.HardBounce,
                EmailSuppression.SystemDeliveryEvent,
                null,
                null,
                evidenceKey: "evt-1"
            );

            Assert.True(replay.Applied);
            Assert.True(replay.Suppression.IsActive);
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
            var outcome = await service.RecordAsync(
                "jane@example.com",
                SuppressionReason.ResidentRequest,
                "ShortLink",
                null,
                null
            );

            Assert.True(outcome.Applied);
            Assert.Equal(2, outcome.Suppression.ConsecutiveFailureCount);
            repo.Verify(r => r.UpdateAsync(It.IsAny<EmailSuppression>()), Times.Once);
        }

        [Fact]
        public async Task Record_SurfacesExhaustedRacesAsAConcurrencyConflict()
        {
            // The same type the repository throws, not a wrapper: "the write kept losing races"
            // is still a concurrency conflict, and request-path callers answer it with 409 -
            // the old WithOptimisticRetry contract - rather than a 500 that pages someone.
            var repo = new Mock<IEmailSuppressionRepository>();
            repo.Setup(r => r.GetByEmailAsync(It.IsAny<string>())).ReturnsAsync((EmailSuppression)null);
            repo.Setup(r => r.AddAsync(It.IsAny<EmailSuppression>()))
                .ThrowsAsync(new ConcurrencyConflictException("exists", new InvalidOperationException()));

            var service = new EmailSuppressionService(repo.Object, _time);

            await Assert.ThrowsAsync<ConcurrencyConflictException>(
                () =>
                    service.RecordAsync(
                        "jane@example.com",
                        SuppressionReason.HardBounce,
                        EmailSuppression.SystemDeliveryEvent,
                        null,
                        null
                    )
            );
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
