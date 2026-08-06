using System;
using System.Threading;
using System.Threading.Tasks;
using Web.Services;
using Xunit;

namespace Web.UnitTests;

public sealed class JobIntervalTests
{
    // The invariant: nothing JobInterval produces may make Task.Delay throw, because an
    // ArgumentOutOfRangeException escaping a BackgroundService loop stops the whole host.
    private static async Task AssertUsableAsDelay(TimeSpan interval)
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(interval, cts.Token));
    }

    [Theory]
    [InlineData(24)]
    [InlineData(8760)] // "yearly" typo - 365 days, far past Task.Delay's ~49.7 day ceiling
    [InlineData(int.MaxValue)] // would overflow TimeSpan.FromHours itself
    public async Task FromHours_never_produces_a_delay_Task_Delay_rejects(int hours)
    {
        var interval = JobInterval.FromHours(hours);

        Assert.True(interval <= JobInterval.MaxDelay);
        await AssertUsableAsDelay(interval);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(int.MaxValue)]
    public async Task FromMinutes_never_produces_a_delay_Task_Delay_rejects(int minutes)
    {
        var interval = JobInterval.FromMinutes(minutes);

        Assert.True(interval <= JobInterval.MaxDelay);
        await AssertUsableAsDelay(interval);
    }

    [Fact]
    public void FromHours_and_FromMinutes_clamp_the_lower_bound_to_the_supplied_minimum()
    {
        Assert.Equal(TimeSpan.FromHours(1), JobInterval.FromHours(0));
        Assert.Equal(TimeSpan.FromHours(1), JobInterval.FromHours(-5));
        Assert.Equal(TimeSpan.FromMinutes(1), JobInterval.FromMinutes(-1));
    }

    [Fact]
    public void FromSeconds_allows_zero_so_tests_can_disable_the_startup_delay()
    {
        Assert.Equal(TimeSpan.Zero, JobInterval.FromSeconds(0));
        Assert.Equal(TimeSpan.Zero, JobInterval.FromSeconds(-3));
    }

    [Fact]
    public void Clamp_bounds_a_computed_remainder_on_both_ends()
    {
        Assert.Equal(JobInterval.MaxDelay, JobInterval.Clamp(TimeSpan.FromDays(365)));
        Assert.Equal(TimeSpan.Zero, JobInterval.Clamp(TimeSpan.FromHours(-1)));
        Assert.Equal(TimeSpan.FromHours(3), JobInterval.Clamp(TimeSpan.FromHours(3)));
    }
}
