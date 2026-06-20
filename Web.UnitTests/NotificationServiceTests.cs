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

        Assert.Equal(Notification.DeterministicId(NotificationType.HeldMessage, "heldMessage", "held-1"), result.Id);
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
        // and the re-query then returns the notification the winning caller persisted.
        var winner = new Notification
        {
            Id = Guid.NewGuid(),
            Type = NotificationType.Registration,
            AudienceKey = Audience,
            TargetType = "user",
            TargetId = "user-1",
        };
        var repo = new Mock<INotificationRepository>();
        repo.SetupSequence(r => r.GetByTargetAsync("user", "user-1"))
            .ReturnsAsync((Notification?)null)
            .ReturnsAsync(winner);
        repo.Setup(r => r.AddAsync(It.IsAny<Notification>()))
            .ThrowsAsync(new CosmosException("conflict", HttpStatusCode.Conflict, 0, string.Empty, 0));

        var notifier = new Mock<INotificationRealtimeNotifier>();
        var service = new NotificationService(repo.Object, notifier.Object, NullLogger<NotificationService>.Instance);

        var result = await service.RaiseAsync(NotificationType.Registration, Audience, "user", "user-1", "New user", "Jane");

        // The loser returns the winner's notification, not a duplicate...
        Assert.Same(winner, result);
        // ...and must not emit a second "changed" signal for the same target.
        notifier.Verify(n => n.NotifyAudienceChangedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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
