using System;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Web.MockData;
using Web.Models;
using Xunit;

namespace Web.UnitTests;

/// <summary>
/// Covers the optimistic-concurrency path on the notification repository mock, which must mirror the
/// Cosmos If-Match behavior used by escalation stamping.
/// </summary>
public sealed class MockNotificationRepositoryEtagTests
{
    private static Notification NewNotification() => new()
    {
        Id = Guid.NewGuid(),
        AudienceKey = NotificationAudience.Administrators,
        TargetType = NotificationTargetType.User,
        TargetId = Guid.NewGuid().ToString(),
        CreatedUtc = DateTime.UtcNow,
    };

    [Fact]
    public async Task UpsertWithEtag_succeeds_when_etag_matches()
    {
        var repo = new MockNotificationRepository();
        var n = NewNotification();
        await repo.AddAsync(n);

        var fresh = await repo.GetByIdAsync(n.Id);
        fresh!.EscalatedUtc = DateTime.UtcNow;

        await repo.UpsertWithEtagAsync(fresh); // current etag — should not throw

        var stored = await repo.GetByIdAsync(n.Id);
        Assert.NotNull(stored!.EscalatedUtc);
    }

    [Fact]
    public async Task UpsertWithEtag_throws_412_when_etag_is_stale()
    {
        var repo = new MockNotificationRepository();
        var n = NewNotification();
        await repo.AddAsync(n);

        var stale = await repo.GetByIdAsync(n.Id); // captures etag E1

        // A concurrent writer moves the document forward (etag E2).
        var concurrent = await repo.GetByIdAsync(n.Id);
        concurrent!.ResolvedUtc = DateTime.UtcNow;
        await repo.UpsertAsync(concurrent);

        stale!.EscalatedUtc = DateTime.UtcNow;
        var ex = await Assert.ThrowsAsync<CosmosException>(() => repo.UpsertWithEtagAsync(stale));
        Assert.Equal(HttpStatusCode.PreconditionFailed, ex.StatusCode);

        // The concurrent resolve is not clobbered.
        var stored = await repo.GetByIdAsync(n.Id);
        Assert.NotNull(stored!.ResolvedUtc);
        Assert.Null(stored.EscalatedUtc);
    }
}
