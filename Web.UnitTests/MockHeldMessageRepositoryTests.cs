using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Web.MockData;
using Web.Models;
using Xunit;

namespace Web.UnitTests;

public sealed class MockHeldMessageRepositoryTests
{
    private static HeldMessage Msg(string committeeId, HeldMessageStatus status, DateTime heldUtc) =>
        new HeldMessage
        {
            Id = Guid.NewGuid(),
            CommitteeId = committeeId,
            CommitteeEmail = $"{committeeId}@cohad.org",
            InternetMessageId = $"<{Guid.NewGuid()}@example.com>",
            Subject = "s",
            Status = status,
            ReceivedUtc = heldUtc,
            HeldUtc = heldUtc,
        };

    [Fact]
    public async Task AddAsync_throws_conflict_on_duplicate_id_and_keeps_original()
    {
        var repo = new MockHeldMessageRepository();
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var original = Msg("board", HeldMessageStatus.Held, baseTime);
        await repo.AddAsync(original);

        // A second add with the same id must 409 (matching Cosmos CreateItemAsync), not overwrite. The
        // poller assigns deterministic ids and depends on this 409 to dedup concurrent holds.
        var duplicate = Msg("board", HeldMessageStatus.Approved, baseTime);
        duplicate.Id = original.Id;
        var ex = await Assert.ThrowsAsync<CosmosException>(() => repo.AddAsync(duplicate));
        Assert.Equal(HttpStatusCode.Conflict, ex.StatusCode);

        // The original record is untouched (status not reset, no re-quarantine).
        var stored = await repo.GetByIdAsync(original.Id);
        Assert.NotNull(stored);
        Assert.Equal(HeldMessageStatus.Held, stored!.Status);
    }

    [Fact]
    public async Task GetByCommitteeIdAsync_status_filter_is_applied_before_the_limit()
    {
        var repo = new MockHeldMessageRepository();
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // The two most recent rows are resolved; the only Held row is older.
        await repo.AddAsync(Msg("board", HeldMessageStatus.Approved, baseTime.AddMinutes(30)));
        await repo.AddAsync(Msg("board", HeldMessageStatus.Rejected, baseTime.AddMinutes(20)));
        await repo.AddAsync(Msg("board", HeldMessageStatus.Held, baseTime.AddMinutes(10)));

        // With a limit smaller than the total, an unfiltered query would return only the two
        // resolved rows and miss the Held one. Filtering by status must prevent that.
        var held = await repo.GetByCommitteeIdAsync("board", limit: 2, status: HeldMessageStatus.Held);

        Assert.Single(held);
        Assert.Equal(HeldMessageStatus.Held, held[0].Status);
    }

    [Fact]
    public async Task GetByCommitteeIdAsync_without_status_returns_all_statuses()
    {
        var repo = new MockHeldMessageRepository();
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await repo.AddAsync(Msg("board", HeldMessageStatus.Approved, baseTime.AddMinutes(2)));
        await repo.AddAsync(Msg("board", HeldMessageStatus.Held, baseTime.AddMinutes(1)));

        var all = await repo.GetByCommitteeIdAsync("board");

        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task GetAwaitingNotificationAsync_returns_only_unnotified_held_messages_past_cutoff()
    {
        var repo = new MockHeldMessageRepository();
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var cutoff = baseTime.AddMinutes(15);

        // Eligible: held, un-notified, held before the cutoff.
        var eligible = Msg("board", HeldMessageStatus.Held, baseTime.AddMinutes(10));
        await repo.AddAsync(eligible);
        // Too recent: held after the cutoff (still inside the antispam window).
        await repo.AddAsync(Msg("board", HeldMessageStatus.Held, baseTime.AddMinutes(20)));
        // Already notified: excluded even though it is old enough.
        var notified = Msg("board", HeldMessageStatus.Held, baseTime.AddMinutes(5));
        notified.NotifiedUtc = baseTime.AddMinutes(8);
        await repo.AddAsync(notified);
        // Resolved: not Held, so excluded.
        await repo.AddAsync(Msg("board", HeldMessageStatus.Approved, baseTime.AddMinutes(1)));

        var awaiting = await repo.GetAwaitingNotificationAsync(cutoff);

        Assert.Single(awaiting);
        Assert.Equal(eligible.Id, awaiting[0].Id);
    }

    [Fact]
    public async Task GetAwaitingNotificationAsync_orders_oldest_held_first()
    {
        var repo = new MockHeldMessageRepository();
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var older = Msg("board", HeldMessageStatus.Held, baseTime.AddMinutes(1));
        var newer = Msg("board", HeldMessageStatus.Held, baseTime.AddMinutes(5));
        await repo.AddAsync(newer);
        await repo.AddAsync(older);

        var awaiting = await repo.GetAwaitingNotificationAsync(baseTime.AddMinutes(10));

        Assert.Equal(new[] { older.Id, newer.Id }, awaiting.Select(m => m.Id).ToArray());
    }
}
