using System;
using System.Linq;
using System.Threading.Tasks;
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
}
