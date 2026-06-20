using System;
using System.Threading.Tasks;
using Web.MockData;
using Web.Models;
using Xunit;

namespace Web.UnitTests;

public sealed class MockNotificationDigestStateRepositoryTests
{
    [Fact]
    public async Task Get_returns_null_when_never_digested()
    {
        var repo = new MockNotificationDigestStateRepository();

        Assert.Null(await repo.GetAsync("nobody@example.com"));
    }

    [Fact]
    public async Task Upsert_then_get_roundtrips()
    {
        var repo = new MockNotificationDigestStateRepository();
        var when = DateTime.UtcNow;

        await repo.UpsertAsync(new NotificationDigestState { RecipientEmail = "admin@example.com", LastDigestUtc = when });

        var state = await repo.GetAsync("admin@example.com");
        Assert.NotNull(state);
        Assert.Equal(when, state!.LastDigestUtc);
    }

    [Fact]
    public async Task Get_is_case_insensitive_on_email()
    {
        var repo = new MockNotificationDigestStateRepository();
        var when = DateTime.UtcNow;
        await repo.UpsertAsync(new NotificationDigestState { RecipientEmail = "Admin@Example.com", LastDigestUtc = when });

        var state = await repo.GetAsync("admin@example.com");
        Assert.NotNull(state);
        Assert.Equal(when, state!.LastDigestUtc);
    }

    [Fact]
    public async Task Upsert_replaces_existing_state()
    {
        var repo = new MockNotificationDigestStateRepository();
        await repo.UpsertAsync(new NotificationDigestState { RecipientEmail = "a@example.com", LastDigestUtc = DateTime.UtcNow.AddHours(-5) });
        var later = DateTime.UtcNow;
        await repo.UpsertAsync(new NotificationDigestState { RecipientEmail = "a@example.com", LastDigestUtc = later });

        var state = await repo.GetAsync("a@example.com");
        Assert.Equal(later, state!.LastDigestUtc);
    }
}
