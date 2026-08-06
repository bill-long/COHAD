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

        var scheduler = Create(runner.Object, new MockBackgroundJobStateRepository(), options);

        Assert.False(await scheduler.RunIfDueAsync(CancellationToken.None));
        runner.Verify(r => r.RunAsync(It.IsAny<CancellationToken>()), Times.Never);
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
            }
        );

        var scheduler = Create(runner.Object, jobState, ValidOptions());

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
