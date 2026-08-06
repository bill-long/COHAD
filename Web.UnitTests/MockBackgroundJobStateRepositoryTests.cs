using System;
using System.Threading.Tasks;
using Web.MockData;
using Web.Models;
using Xunit;

namespace Web.UnitTests;

public sealed class MockBackgroundJobStateRepositoryTests
{
    [Fact]
    public async Task Get_returns_null_when_the_job_has_never_run()
    {
        var repo = new MockBackgroundJobStateRepository();

        Assert.Null(await repo.GetAsync("paypal-sync"));
    }

    [Fact]
    public async Task Upsert_then_get_roundtrips_both_timestamps()
    {
        var repo = new MockBackgroundJobStateRepository();
        var success = DateTime.UtcNow.AddDays(-2);
        var attempt = DateTime.UtcNow.AddHours(-1);

        await repo.UpsertAsync(
            new BackgroundJobState
            {
                JobName = "paypal-sync",
                LastSuccessUtc = success,
                LastAttemptUtc = attempt,
            }
        );

        var state = await repo.GetAsync("paypal-sync");
        Assert.NotNull(state);
        Assert.Equal(success, state!.LastSuccessUtc);
        Assert.Equal(attempt, state.LastAttemptUtc);
        Assert.NotNull(state.ETag);
    }

    [Fact]
    public async Task Job_name_lookup_is_case_and_whitespace_insensitive()
    {
        var repo = new MockBackgroundJobStateRepository();
        await repo.UpsertAsync(new BackgroundJobState { JobName = "PayPal-Sync" });

        Assert.NotNull(await repo.GetAsync("  paypal-sync  "));
    }

    [Fact]
    public async Task Upsert_ignores_a_blank_job_name()
    {
        var repo = new MockBackgroundJobStateRepository();

        await repo.UpsertAsync(new BackgroundJobState { JobName = "   " });

        Assert.Null(await repo.GetAsync("   "));
    }
}
