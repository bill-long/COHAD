using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Web.MockData;
using Web.Models;
using Web.Services;
using Web.Services.Repositories;
using Xunit;

namespace Web.UnitTests;

public sealed class PayPalSyncSchedulerTests
{
    private static PayPalOptions ValidOptions() =>
        new()
        {
            SyncEnabled = true,
            ClientId = "client",
            ClientSecret = "secret",
            SyncIntervalDays = 7,
            SyncRetryIntervalHours = 6,
        };

    private static PayPalSyncScheduler Create(
        IPayPalPaymentSyncRunner runner,
        IBackgroundJobStateRepository jobState,
        PayPalOptions options
    ) =>
        new(
            runner,
            jobState,
            Options.Create(options),
            NullLogger<PayPalSyncScheduler>.Instance
        );

    private static Mock<IPayPalPaymentSyncRunner> SucceedingRunner()
    {
        var runner = new Mock<IPayPalPaymentSyncRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new PayPalPaymentSyncResult());
        return runner;
    }

    [Fact]
    public async Task RunIfDue_runs_and_stamps_success_when_never_run_before()
    {
        var runner = SucceedingRunner();
        var jobState = new MockBackgroundJobStateRepository();
        var scheduler = Create(runner.Object, jobState, ValidOptions());

        Assert.True(await scheduler.RunIfDueAsync(CancellationToken.None));

        runner.Verify(r => r.RunAsync(It.IsAny<CancellationToken>()), Times.Once);
        var state = await jobState.GetAsync(PayPalSyncScheduler.JobName);
        Assert.NotNull(state);
        Assert.True(state!.LastSuccessUtc > DateTime.UtcNow.AddMinutes(-1));
        Assert.True(state.LastAttemptUtc > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task RunIfDue_does_not_run_within_the_sync_interval()
    {
        var runner = new Mock<IPayPalPaymentSyncRunner>(MockBehavior.Strict);
        var jobState = new MockBackgroundJobStateRepository();
        await jobState.UpsertAsync(
            new BackgroundJobState
            {
                JobName = PayPalSyncScheduler.JobName,
                LastSuccessUtc = DateTime.UtcNow.AddDays(-3),
                LastAttemptUtc = DateTime.UtcNow.AddDays(-3),
            }
        );

        var scheduler = Create(runner.Object, jobState, ValidOptions());

        Assert.False(await scheduler.RunIfDueAsync(CancellationToken.None));
        runner.Verify(r => r.RunAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunIfDue_runs_once_the_sync_interval_has_elapsed()
    {
        var runner = SucceedingRunner();
        var jobState = new MockBackgroundJobStateRepository();
        await jobState.UpsertAsync(
            new BackgroundJobState
            {
                JobName = PayPalSyncScheduler.JobName,
                LastSuccessUtc = DateTime.UtcNow.AddDays(-8),
                LastAttemptUtc = DateTime.UtcNow.AddDays(-8),
            }
        );

        var scheduler = Create(runner.Object, jobState, ValidOptions());

        Assert.True(await scheduler.RunIfDueAsync(CancellationToken.None));
        runner.Verify(r => r.RunAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunIfDue_does_not_run_when_sync_disabled()
    {
        var runner = new Mock<IPayPalPaymentSyncRunner>(MockBehavior.Strict);
        var options = ValidOptions();
        options.SyncEnabled = false;

        var scheduler = Create(runner.Object, new MockBackgroundJobStateRepository(), options);

        Assert.False(await scheduler.RunIfDueAsync(CancellationToken.None));
        runner.Verify(r => r.RunAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunIfDue_does_not_run_when_credentials_are_missing()
    {
        var runner = new Mock<IPayPalPaymentSyncRunner>(MockBehavior.Strict);
        var options = ValidOptions();
        options.ClientSecret = "";
        var jobState = new MockBackgroundJobStateRepository();

        var scheduler = Create(runner.Object, jobState, options);

        Assert.False(await scheduler.RunIfDueAsync(CancellationToken.None));
        runner.Verify(r => r.RunAsync(It.IsAny<CancellationToken>()), Times.Never);

        // Recorded as a failed attempt, so the retry gate throttles the warning to once per retry
        // interval. An hourly tick that warned every time would emit thousands of traces a year.
        var state = await jobState.GetAsync(PayPalSyncScheduler.JobName);
        Assert.NotNull(state);
        Assert.True(state!.LastAttemptUtc > DateTime.UtcNow.AddMinutes(-1));
        Assert.Equal(DateTime.MinValue, state.LastSuccessUtc);
    }

    [Fact]
    public async Task Missing_credentials_back_off_instead_of_warning_on_every_tick()
    {
        var runner = new Mock<IPayPalPaymentSyncRunner>(MockBehavior.Strict);
        var options = ValidOptions();
        options.ClientSecret = "";
        var jobState = new MockBackgroundJobStateRepository();
        var scheduler = Create(runner.Object, jobState, options);

        Assert.False(await scheduler.RunIfDueAsync(CancellationToken.None));
        var afterFirst = (await jobState.GetAsync(PayPalSyncScheduler.JobName))!.LastAttemptUtc;

        // The immediately following tick is inside the retry interval, so it returns without re-stamping.
        Assert.False(await scheduler.RunIfDueAsync(CancellationToken.None));
        Assert.Equal(afterFirst, (await jobState.GetAsync(PayPalSyncScheduler.JobName))!.LastAttemptUtc);
    }

    [Fact]
    public async Task A_future_dated_success_stamp_does_not_disable_the_retry_backoff()
    {
        // The backoff reads the persisted LastAttemptFailed flag. Deriving "failed" from
        // LastAttemptUtc > LastSuccessUtc inverts when LastSuccessUtc is future-dated - so a skewed clock
        // would remove the backoff entirely and drive an API call every tick, forever.
        var runner = new Mock<IPayPalPaymentSyncRunner>(MockBehavior.Strict);
        var jobState = new MockBackgroundJobStateRepository();
        await jobState.UpsertAsync(
            new BackgroundJobState
            {
                JobName = PayPalSyncScheduler.JobName,
                LastSuccessUtc = DateTime.UtcNow.AddYears(1), // future-dated: sync reads as due
                LastAttemptUtc = DateTime.UtcNow.AddHours(-1), // failed an hour ago
                LastAttemptFailed = true,
            }
        );

        var scheduler = Create(runner.Object, jobState, ValidOptions());

        Assert.False(await scheduler.RunIfDueAsync(CancellationToken.None));
        runner.Verify(r => r.RunAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_successful_run_clears_the_failed_flag()
    {
        var runner = SucceedingRunner();
        var jobState = new MockBackgroundJobStateRepository();
        await jobState.UpsertAsync(
            new BackgroundJobState
            {
                JobName = PayPalSyncScheduler.JobName,
                LastSuccessUtc = DateTime.UtcNow.AddDays(-8),
                LastAttemptUtc = DateTime.UtcNow.AddDays(-7),
                LastAttemptFailed = true,
            }
        );

        var scheduler = Create(runner.Object, jobState, ValidOptions());

        Assert.True(await scheduler.RunIfDueAsync(CancellationToken.None));

        var state = await jobState.GetAsync(PayPalSyncScheduler.JobName);
        Assert.NotNull(state);
        Assert.False(state!.LastAttemptFailed);
    }

    [Fact]
    public async Task A_throwing_run_leaves_the_failed_flag_set()
    {
        var runner = new Mock<IPayPalPaymentSyncRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException());
        var jobState = new MockBackgroundJobStateRepository();
        var scheduler = Create(runner.Object, jobState, ValidOptions());

        await Assert.ThrowsAsync<InvalidOperationException>(() => scheduler.RunIfDueAsync(CancellationToken.None));

        var state = await jobState.GetAsync(PayPalSyncScheduler.JobName);
        Assert.NotNull(state);
        Assert.True(state!.LastAttemptFailed);
    }

    [Fact]
    public async Task A_future_dated_attempt_stamp_does_not_latch_the_sync_off()
    {
        var runner = SucceedingRunner();
        var jobState = new MockBackgroundJobStateRepository();
        await jobState.UpsertAsync(
            new BackgroundJobState
            {
                JobName = PayPalSyncScheduler.JobName,
                LastSuccessUtc = DateTime.UtcNow.AddDays(-30),
                LastAttemptUtc = DateTime.UtcNow.AddYears(1),
                LastAttemptFailed = true,
            }
        );

        var scheduler = Create(runner.Object, jobState, ValidOptions());

        Assert.True(await scheduler.RunIfDueAsync(CancellationToken.None));
        runner.Verify(r => r.RunAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunIfDue_stamps_attempt_but_not_success_when_the_run_throws()
    {
        var runner = new Mock<IPayPalPaymentSyncRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("boom"));
        var jobState = new MockBackgroundJobStateRepository();
        var scheduler = Create(runner.Object, jobState, ValidOptions());

        await Assert.ThrowsAsync<InvalidOperationException>(() => scheduler.RunIfDueAsync(CancellationToken.None));

        var state = await jobState.GetAsync(PayPalSyncScheduler.JobName);
        Assert.NotNull(state);
        Assert.True(state!.LastAttemptUtc > DateTime.UtcNow.AddMinutes(-1));
        // Success is never stamped, so the sync stays due rather than waiting out a full interval.
        Assert.Equal(DateTime.MinValue, state.LastSuccessUtc);
    }

    [Fact]
    public async Task RunIfDue_backs_off_within_the_retry_interval_after_a_failure()
    {
        var runner = new Mock<IPayPalPaymentSyncRunner>(MockBehavior.Strict);
        var jobState = new MockBackgroundJobStateRepository();
        // Due (never succeeded) but attempted an hour ago: the retry interval is 6h, so it must wait.
        await jobState.UpsertAsync(
            new BackgroundJobState
            {
                JobName = PayPalSyncScheduler.JobName,
                LastSuccessUtc = DateTime.MinValue,
                LastAttemptUtc = DateTime.UtcNow.AddHours(-1),
                LastAttemptFailed = true,
            }
        );

        var scheduler = Create(runner.Object, jobState, ValidOptions());

        Assert.False(await scheduler.RunIfDueAsync(CancellationToken.None));
        runner.Verify(r => r.RunAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_long_retry_interval_does_not_throttle_the_normal_cadence_after_a_success()
    {
        // The retry backoff must gate only *failed* attempts. LastAttemptUtc is stamped for successful
        // runs too, so gating on it unconditionally would let SyncRetryIntervalHours silently override
        // the configured SyncIntervalDays - halving the import cadence with nothing logged.
        var runner = SucceedingRunner();
        var jobState = new MockBackgroundJobStateRepository();
        var lastRun = DateTime.UtcNow.AddDays(-8);
        await jobState.UpsertAsync(
            new BackgroundJobState
            {
                JobName = PayPalSyncScheduler.JobName,
                LastSuccessUtc = lastRun,
                LastAttemptUtc = lastRun, // a successful run stamps both
            }
        );

        var options = ValidOptions();
        options.SyncIntervalDays = 7;
        options.SyncRetryIntervalHours = 336; // 14 days - longer than the sync interval

        var scheduler = Create(runner.Object, jobState, options);

        Assert.True(await scheduler.RunIfDueAsync(CancellationToken.None));
        runner.Verify(r => r.RunAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_sync_interval_beyond_the_Task_Delay_ceiling_is_not_silently_capped()
    {
        // SyncIntervalDays is a comparison window, never a Task.Delay argument, so the ~49.7 day delay
        // ceiling must not apply to it - a quarterly cadence must stay quarterly.
        var runner = new Mock<IPayPalPaymentSyncRunner>(MockBehavior.Strict);
        var jobState = new MockBackgroundJobStateRepository();
        await jobState.UpsertAsync(
            new BackgroundJobState
            {
                JobName = PayPalSyncScheduler.JobName,
                LastSuccessUtc = DateTime.UtcNow.AddDays(-60),
                LastAttemptUtc = DateTime.UtcNow.AddDays(-60),
            }
        );

        var options = ValidOptions();
        options.SyncIntervalDays = 90;

        var scheduler = Create(runner.Object, jobState, options);

        // 60 days elapsed against a 90-day interval: not due. A clamp to 49.7 days would have run it.
        Assert.False(await scheduler.RunIfDueAsync(CancellationToken.None));
        runner.Verify(r => r.RunAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunIfDue_retries_once_the_retry_interval_has_elapsed()
    {
        var runner = SucceedingRunner();
        var jobState = new MockBackgroundJobStateRepository();
        await jobState.UpsertAsync(
            new BackgroundJobState
            {
                JobName = PayPalSyncScheduler.JobName,
                LastSuccessUtc = DateTime.MinValue,
                LastAttemptUtc = DateTime.UtcNow.AddHours(-7),
                LastAttemptFailed = true,
            }
        );

        var scheduler = Create(runner.Object, jobState, ValidOptions());

        Assert.True(await scheduler.RunIfDueAsync(CancellationToken.None));
        runner.Verify(r => r.RunAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunIfDue_observes_cancellation()
    {
        var runner = new Mock<IPayPalPaymentSyncRunner>(MockBehavior.Strict);
        var scheduler = Create(runner.Object, new MockBackgroundJobStateRepository(), ValidOptions());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scheduler.RunIfDueAsync(cts.Token));
        runner.Verify(r => r.RunAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
