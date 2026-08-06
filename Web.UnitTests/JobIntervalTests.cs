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
    [InlineData(60)]
    [InlineData(525600)] // "yearly" typo - 365 days, far past Task.Delay's ~49.7 day ceiling
    [InlineData(int.MaxValue)] // would overflow TimeSpan.FromMinutes itself
    public async Task FromMinutes_never_produces_a_delay_Task_Delay_rejects(int minutes)
    {
        var interval = JobInterval.FromMinutes(minutes);

        Assert.True(interval <= JobInterval.MaxDelay);
        await AssertUsableAsDelay(interval);
    }

    [Fact]
    public void FromMinutes_clamps_the_lower_bound_to_the_supplied_minimum()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), JobInterval.FromMinutes(0));
        Assert.Equal(TimeSpan.FromMinutes(1), JobInterval.FromMinutes(-1));
    }

    [Fact]
    public void FromSeconds_allows_zero_so_tests_can_disable_the_startup_delay()
    {
        Assert.Equal(TimeSpan.Zero, JobInterval.FromSeconds(0));
        Assert.Equal(TimeSpan.Zero, JobInterval.FromSeconds(-3));
    }

    [Theory]
    [InlineData(90)] // quarterly - well past the ~49.7 day Task.Delay ceiling
    [InlineData(365)]
    public void WindowFromDays_does_not_apply_the_Task_Delay_ceiling(int days)
    {
        // Comparison windows are never delays. Clamping them to MaxDelay would silently turn a quarterly
        // cadence into a 7-weekly one.
        Assert.Equal(TimeSpan.FromDays(days), JobInterval.WindowFromDays(days));
        Assert.True(JobInterval.WindowFromDays(days) > JobInterval.MaxDelay);
    }

    [Fact]
    public void WindowFromHours_does_not_apply_the_Task_Delay_ceiling()
    {
        Assert.Equal(TimeSpan.FromHours(2160), JobInterval.WindowFromHours(2160)); // 90 days
    }

    [Fact]
    public void WindowFromHours_and_WindowFromDays_agree_on_the_same_duration()
    {
        // Locks the hours/days conversion in the shared ceiling, which a factor-of-24 slip would break
        // while both calls still returned a constructible TimeSpan.
        Assert.Equal(JobInterval.WindowFromDays(90), JobInterval.WindowFromHours(90 * 24));
        Assert.Equal(JobInterval.WindowFromDays(int.MaxValue), JobInterval.WindowFromHours(int.MaxValue));
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(-5)]
    public void Window_helpers_never_throw_on_an_out_of_range_value(int value)
    {
        // The only guard they owe is against TimeSpan.From*(int) throwing, which it does well before
        // TimeSpan.MaxValue - so the assertion is that construction succeeded and honoured the floor.
        Assert.True(JobInterval.WindowFromDays(value) >= TimeSpan.FromDays(1));
        Assert.True(JobInterval.WindowFromHours(value) >= TimeSpan.FromHours(1));
    }

    [Fact]
    public void Clamp_bounds_a_computed_remainder_on_both_ends()
    {
        Assert.Equal(JobInterval.MaxDelay, JobInterval.Clamp(TimeSpan.FromDays(365)));
        Assert.Equal(TimeSpan.Zero, JobInterval.Clamp(TimeSpan.FromHours(-1)));
        Assert.Equal(TimeSpan.FromHours(3), JobInterval.Clamp(TimeSpan.FromHours(3)));
    }
}
