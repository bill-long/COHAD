using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Web.MockData;
using Web.Models;
using Web.Services;
using Web.Services.Repositories;
using Xunit;

namespace Web.UnitTests;

public sealed class NotificationServiceTests
{
    private const string Audience = "role:Administrator";

    private static (NotificationService service, INotificationRepository repo, Mock<INotificationRealtimeNotifier> notifier) CreateService(
        INotificationRepository? repo = null
    )
    {
        var repository = repo ?? new MockNotificationRepository();
        var notifier = new Mock<INotificationRealtimeNotifier>();
        notifier
            .Setup(n => n.NotifyAudienceChangedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var service = new NotificationService(repository, notifier.Object, NullLogger<NotificationService>.Instance);
        return (service, repository, notifier);
    }

    /// <summary>
    /// Wraps the mock repository and, when armed, applies a competing escalation stamp right before
    /// the next ETag-conditional write — simulating the escalation sweep winning the race — so that
    /// write fails with 412 and the service must re-read instead of clobbering the stamp.
    /// </summary>
    private sealed class StampRacingRepository : INotificationRepository
    {
        private readonly MockNotificationRepository _inner;

        public StampRacingRepository(MockNotificationRepository inner) => _inner = inner;

        public bool StampBeforeNextConditionalWrite { get; set; }

        public Task AddAsync(Notification notification) => _inner.AddAsync(notification);

        public Task<Notification?> GetByIdAsync(Guid id) => _inner.GetByIdAsync(id);

        public Task<Notification?> GetByTargetAsync(string targetType, string targetId) =>
            _inner.GetByTargetAsync(targetType, targetId);

        public Task UpsertAsync(Notification notification) => _inner.UpsertAsync(notification);

        public async Task UpsertWithEtagAsync(Notification notification)
        {
            if (StampBeforeNextConditionalWrite)
            {
                StampBeforeNextConditionalWrite = false;
                var current = await _inner.GetByIdAsync(notification.Id)
                    ?? throw new InvalidOperationException("Race target vanished.");
                current.EscalatedUtc = DateTime.UtcNow;
                current.EscalationJobId = Guid.NewGuid();
                await _inner.UpsertWithEtagAsync(current); // bumps the stored ETag → caller's write 412s
            }

            await _inner.UpsertWithEtagAsync(notification);
        }

        public Task<List<Notification>> GetUnresolvedByAudienceAsync(string audienceKey, int limit = 50) =>
            _inner.GetUnresolvedByAudienceAsync(audienceKey, limit);

        public Task<List<Notification>> GetUnescalatedByAudienceOldestFirstAsync(string audienceKey, int limit = 200) =>
            _inner.GetUnescalatedByAudienceOldestFirstAsync(audienceKey, limit);
    }

    [Fact]
    public async Task UnacknowledgeAsync_ReopensAndClearsResolution()
    {
        var (service, _, notifier) = CreateService();
        var raised = await service.RaiseAsync(NotificationType.Registration, Audience, "user", "user-1", "t", "s");
        await service.AcknowledgeAsync(raised.Id, "admin-1");

        var result = await service.UnacknowledgeAsync(raised.Id);

        Assert.NotNull(result);
        Assert.Null(result!.ResolvedUtc);
        Assert.Null(result.ResolvedBy);
        notifier.Verify(n => n.NotifyAudienceChangedAsync(Audience, It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task UnacknowledgeAsync_PreservesConcurrentEscalationStamp()
    {
        var inner = new MockNotificationRepository();
        var racing = new StampRacingRepository(inner);
        var (service, _, _) = CreateService(racing);
        var raised = await service.RaiseAsync(NotificationType.Registration, Audience, "user", "user-1", "t", "s");
        await service.AcknowledgeAsync(raised.Id, "admin-1");

        // The sweep stamps EscalatedUtc between the undo's read and its write.
        racing.StampBeforeNextConditionalWrite = true;
        var result = await service.UnacknowledgeAsync(raised.Id);

        Assert.NotNull(result);
        Assert.Null(result!.ResolvedUtc);
        var stored = await inner.GetByIdAsync(raised.Id);
        Assert.NotNull(stored!.EscalatedUtc); // the stamp survived the undo — no duplicate digest
        Assert.NotNull(stored.EscalationJobId);
    }

    [Fact]
    public async Task AcknowledgeAsync_PreservesConcurrentEscalationStamp()
    {
        var inner = new MockNotificationRepository();
        var racing = new StampRacingRepository(inner);
        var (service, _, _) = CreateService(racing);
        var raised = await service.RaiseAsync(NotificationType.Registration, Audience, "user", "user-1", "t", "s");

        // The sweep stamps EscalatedUtc between the acknowledge's read and its write.
        racing.StampBeforeNextConditionalWrite = true;
        var result = await service.AcknowledgeAsync(raised.Id, "admin-1");

        Assert.NotNull(result);
        Assert.NotNull(result!.ResolvedUtc);
        var stored = await inner.GetByIdAsync(raised.Id);
        Assert.NotNull(stored!.EscalatedUtc); // resolving must not wipe the emailed-at-most-once stamp
        Assert.Equal("admin-1", stored.ResolvedBy);
    }

    [Fact]
    public async Task RaiseAsync_PersistsNotificationAndSignalsAudience()
    {
        var (service, repo, notifier) = CreateService();

        var result = await service.RaiseAsync(
            NotificationType.Registration,
            Audience,
            "user",
            "user-1",
            "New user registered",
            "Jane Doe — 123 Mock Lane"
        );

        Assert.NotEqual(Guid.Empty, result.Id);
        Assert.Null(result.ResolvedUtc);
        Assert.NotEqual(default, result.CreatedUtc);

        var stored = await repo.GetByTargetAsync("user", "user-1");
        Assert.NotNull(stored);
        Assert.Equal(NotificationType.Registration, stored!.Type);
        Assert.Equal("New user registered", stored.Title);
        notifier.Verify(n => n.NotifyAudienceChangedAsync(Audience, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RaiseAsync_IsIdempotentForSameTarget()
    {
        var (service, repo, notifier) = CreateService();

        var first = await service.RaiseAsync(NotificationType.VendorFlag, Audience, "vendorFlag", "flag-1", "Vendor flagged", "Acme");
        var second = await service.RaiseAsync(NotificationType.VendorFlag, Audience, "vendorFlag", "flag-1", "Vendor flagged again", "Acme");

        Assert.Equal(first.Id, second.Id);
        // The second raise returns the original (note the original title, not the new one).
        Assert.Equal("Vendor flagged", second.Title);
        // Only one create signal — the duplicate raise is a no-op.
        notifier.Verify(n => n.NotifyAudienceChangedAsync(Audience, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RaiseAsync_UsesDeterministicIdForTarget()
    {
        var (service, _, _) = CreateService();

        var result = await service.RaiseAsync(NotificationType.HeldMessage, "committee:c1", "heldMessage", "held-1", "Held", "x");

        Assert.Equal(Notification.DeterministicId("heldMessage", "held-1"), result.Id);
    }

    [Theory]
    [InlineData("", "user", "u1")]
    [InlineData("role:Administrator", "", "u1")]
    [InlineData("role:Administrator", "user", "")]
    public async Task RaiseAsync_Throws_WhenRequiredFieldMissing(string audience, string targetType, string targetId)
    {
        var (service, _, _) = CreateService();

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.RaiseAsync(NotificationType.Registration, audience, targetType, targetId, "t", "s")
        );
    }

    [Fact]
    public async Task ResolveAsync_MarksResolvedAndSignals()
    {
        var (service, repo, notifier) = CreateService();
        await service.RaiseAsync(NotificationType.VendorFlag, Audience, "vendorFlag", "flag-1", "Vendor flagged", "Acme");

        var resolved = await service.ResolveAsync("vendorFlag", "flag-1", "admin-7");

        Assert.NotNull(resolved);
        Assert.NotNull(resolved!.ResolvedUtc);
        Assert.Equal("admin-7", resolved.ResolvedBy);

        var stored = await repo.GetByTargetAsync("vendorFlag", "flag-1");
        Assert.NotNull(stored!.ResolvedUtc);
        // One signal for the raise, one for the resolve.
        notifier.Verify(n => n.NotifyAudienceChangedAsync(Audience, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ResolveAsync_ReturnsNull_WhenNoNotificationExists()
    {
        var (service, _, notifier) = CreateService();

        var resolved = await service.ResolveAsync("vendorFlag", "missing", "admin-7");

        Assert.Null(resolved);
        notifier.Verify(n => n.NotifyAudienceChangedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveAsync_IsIdempotent_WhenAlreadyResolved()
    {
        var (service, _, notifier) = CreateService();
        await service.RaiseAsync(NotificationType.VendorFlag, Audience, "vendorFlag", "flag-1", "Vendor flagged", "Acme");
        var first = await service.ResolveAsync("vendorFlag", "flag-1", "admin-7");

        var second = await service.ResolveAsync("vendorFlag", "flag-1", "admin-9");

        Assert.NotNull(second);
        // ResolvedBy is unchanged by the second resolve.
        Assert.Equal("admin-7", second!.ResolvedBy);
        Assert.Equal(first!.ResolvedUtc, second.ResolvedUtc);
        // Raise + first resolve signal; the second resolve is a no-op.
        notifier.Verify(n => n.NotifyAudienceChangedAsync(Audience, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task AcknowledgeAsync_ResolvesById()
    {
        var (service, repo, notifier) = CreateService();
        var raised = await service.RaiseAsync(NotificationType.Registration, Audience, "user", "user-1", "New user", "Jane");

        var acked = await service.AcknowledgeAsync(raised.Id, "admin-1");

        Assert.NotNull(acked);
        Assert.NotNull(acked!.ResolvedUtc);
        Assert.Equal("admin-1", acked.ResolvedBy);
        var stored = await repo.GetByIdAsync(raised.Id);
        Assert.NotNull(stored!.ResolvedUtc);
        notifier.Verify(n => n.NotifyAudienceChangedAsync(Audience, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task AcknowledgeAsync_ReturnsNull_WhenNotificationMissing()
    {
        var (service, _, _) = CreateService();

        var acked = await service.AcknowledgeAsync(Guid.NewGuid(), "admin-1");

        Assert.Null(acked);
    }

    [Fact]
    public async Task GetUnresolvedForAudienceAsync_ReturnsOnlyUnresolvedForThatAudience()
    {
        var (service, _, _) = CreateService();
        await service.RaiseAsync(NotificationType.Registration, Audience, "user", "user-1", "New user", "A");
        await service.RaiseAsync(NotificationType.Registration, Audience, "user", "user-2", "New user", "B");
        await service.RaiseAsync(NotificationType.HeldMessage, "committee:c1", "heldMessage", "held-1", "Held", "C");
        await service.ResolveAsync("user", "user-2", "admin-1");

        var unresolved = await service.GetUnresolvedForAudienceAsync(Audience);

        Assert.Single(unresolved);
        Assert.Equal("user-1", unresolved[0].TargetId);
    }

    [Fact]
    public async Task RaiseAsync_OnConcurrentCreateConflict_ReturnsWinnerWithoutSignaling()
    {
        // Simulate the race window: GetByTarget sees nothing, AddAsync loses the create race (409),
        // and the recovery re-reads the winner by its deterministic id (a point read, not a target query).
        var deterministicId = Notification.DeterministicId("user", "user-1");
        var winner = new Notification
        {
            Id = deterministicId,
            Type = NotificationType.Registration,
            AudienceKey = Audience,
            TargetType = "user",
            TargetId = "user-1",
        };
        var repo = new Mock<INotificationRepository>();
        repo.Setup(r => r.GetByTargetAsync("user", "user-1")).ReturnsAsync((Notification?)null);
        repo.Setup(r => r.AddAsync(It.IsAny<Notification>()))
            .ThrowsAsync(new CosmosException("conflict", HttpStatusCode.Conflict, 0, string.Empty, 0));
        repo.Setup(r => r.GetByIdAsync(deterministicId)).ReturnsAsync(winner);

        var notifier = new Mock<INotificationRealtimeNotifier>();
        var service = new NotificationService(repo.Object, notifier.Object, NullLogger<NotificationService>.Instance);

        var result = await service.RaiseAsync(NotificationType.Registration, Audience, "user", "user-1", "New user", "Jane");

        // The loser returns the winner's notification, not a duplicate...
        Assert.Same(winner, result);
        // ...recovers via the deterministic-id point read...
        repo.Verify(r => r.GetByIdAsync(deterministicId), Times.Once);
        // ...and must not emit a second "changed" signal for the same target.
        notifier.Verify(n => n.NotifyAudienceChangedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// The resolve/acknowledge retry loop must observe cancellation between attempts: under
    /// persistent 412 contention it would otherwise keep issuing Cosmos reads and conditional
    /// writes for a caller that has already given up.
    /// </summary>
    [Fact]
    public async Task Acknowledge_ObservesCancellation_BetweenRetryAttempts()
    {
        var id = Guid.NewGuid();
        using var cts = new CancellationTokenSource();
        var repo = new Mock<INotificationRepository>();
        // Every read returns a fresh unresolved snapshot, so without the per-attempt check the
        // loop would spin through its whole retry budget.
        repo.Setup(r => r.GetByIdAsync(id)).ReturnsAsync(() => new Notification
        {
            Id = id,
            Type = NotificationType.Registration,
            AudienceKey = Audience,
            ETag = Guid.NewGuid().ToString(),
        });
        repo.Setup(r => r.UpsertWithEtagAsync(It.IsAny<Notification>()))
            .Callback(() => cts.Cancel()) // cancellation arrives while the write is losing the race
            .ThrowsAsync(new CosmosException("ETag mismatch.", HttpStatusCode.PreconditionFailed, 0, string.Empty, 0));
        var service = new NotificationService(
            repo.Object, new Mock<INotificationRealtimeNotifier>().Object, NullLogger<NotificationService>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.AcknowledgeAsync(id, "admin-1", cts.Token));

        // One conditional write, then the next attempt noticed the cancellation instead of retrying.
        repo.Verify(r => r.UpsertWithEtagAsync(It.IsAny<Notification>()), Times.Once);
    }

    [Fact]
    public async Task RaiseAsync_DoesNotThrow_WhenSignalFails()
    {
        var repo = new MockNotificationRepository();
        var notifier = new Mock<INotificationRealtimeNotifier>();
        notifier
            .Setup(n => n.NotifyAudienceChangedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("hub down"));
        var service = new NotificationService(repo, notifier.Object, NullLogger<NotificationService>.Instance);

        var result = await service.RaiseAsync(NotificationType.Registration, Audience, "user", "user-1", "New user", "A");

        // The persisted notification survives even though the live signal failed.
        Assert.NotNull(await repo.GetByTargetAsync("user", "user-1"));
        Assert.Equal("user-1", result.TargetId);
    }
}
