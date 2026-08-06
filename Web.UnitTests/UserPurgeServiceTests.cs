using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Web.MockData;
using Web.Models;
using Web.Services;
using Web.Services.Repositories;
using Xunit;

namespace Web.UnitTests;

public sealed class UserPurgeServiceTests
{
    private static (UserPurgeService Service, Mock<IUserRepository> Users) Create(
        UserPurgeOptions options,
        IBackgroundJobStateRepository jobState,
        TaskCompletionSource? ranSignal = null
    )
    {
        var users = new Mock<IUserRepository>();
        users
            .Setup(r => r.GetPurgeCandidatesAsync(It.IsAny<DateTime>(), It.IsAny<int>()))
            .ReturnsAsync(new List<User>())
            .Callback(() => ranSignal?.TrySetResult());

        var services = new ServiceCollection();
        services.AddSingleton(users.Object);
        services.AddSingleton<IAuditLogRepository>(new Mock<IAuditLogRepository>().Object);
        services.AddSingleton(jobState);
        services.AddScoped<UserPurgeRunner>();
        services.AddSingleton<ILogger<UserPurgeRunner>>(NullLogger<UserPurgeRunner>.Instance);

        var provider = services.BuildServiceProvider();
        var service = new UserPurgeService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(options),
            NullLogger<UserPurgeService>.Instance
        );

        return (service, users);
    }

    private static UserPurgeOptions Enabled() =>
        new()
        {
            Enabled = true,
            DryRun = false,
            IntervalHours = 24,
            StartupDelaySeconds = 0,
        };

    [Fact]
    public async Task Runs_immediately_on_startup_rather_than_waiting_out_the_interval()
    {
        // The invariant: the loop checks before it waits. If it delayed first, a deploy cadence shorter
        // than IntervalHours would silently mean the purge never runs at all.
        var ran = new TaskCompletionSource();
        var (service, users) = Create(Enabled(), new MockBackgroundJobStateRepository(), ran);

        await service.StartAsync(CancellationToken.None);
        var completed = await Task.WhenAny(ran.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        await service.StopAsync(CancellationToken.None);

        Assert.Same(ran.Task, completed);
        users.Verify(r => r.GetPurgeCandidatesAsync(It.IsAny<DateTime>(), It.IsAny<int>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task Does_not_purge_again_within_the_interval_after_a_restart()
    {
        // The blast-radius invariant: MaxDeletesPerRun caps deletions per interval, not per process start.
        // A host that recycles several times a day must not purge on every start.
        var jobState = new MockBackgroundJobStateRepository();
        await jobState.UpsertAsync(
            new BackgroundJobState
            {
                JobName = UserPurgeService.JobName,
                LastSuccessUtc = DateTime.UtcNow.AddHours(-2),
                LastAttemptUtc = DateTime.UtcNow.AddHours(-2),
            }
        );

        var (service, users) = Create(Enabled(), jobState);

        var waited = await service.RunIfDueAsync(CancellationToken.None);

        users.Verify(r => r.GetPurgeCandidatesAsync(It.IsAny<DateTime>(), It.IsAny<int>()), Times.Never);
        // Waits out only the remainder of the interval, not a fresh full one.
        Assert.InRange(waited, TimeSpan.FromHours(21.5), TimeSpan.FromHours(22.5));
    }

    [Fact]
    public async Task An_interrupted_sweep_still_consumes_the_interval()
    {
        // The attempt is stamped before any deleting, so a host shutdown partway through a sweep (which
        // cancels UserPurgeRunner between candidates, after some irreversible deletes) cannot let the next
        // process start purge again with a fresh MaxDeletesPerRun budget.
        var jobState = new MockBackgroundJobStateRepository();
        var users = new Mock<IUserRepository>();
        users
            .Setup(r => r.GetPurgeCandidatesAsync(It.IsAny<DateTime>(), It.IsAny<int>()))
            .ThrowsAsync(new OperationCanceledException());

        var services = new ServiceCollection();
        services.AddSingleton(users.Object);
        services.AddSingleton<IAuditLogRepository>(new Mock<IAuditLogRepository>().Object);
        services.AddSingleton<IBackgroundJobStateRepository>(jobState);
        services.AddScoped<UserPurgeRunner>();
        services.AddSingleton<ILogger<UserPurgeRunner>>(NullLogger<UserPurgeRunner>.Instance);
        var provider = services.BuildServiceProvider();

        var service = new UserPurgeService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(Enabled()),
            NullLogger<UserPurgeService>.Instance
        );

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.RunIfDueAsync(CancellationToken.None)
        );

        var state = await jobState.GetAsync(UserPurgeService.JobName);
        Assert.NotNull(state);
        Assert.True(state!.LastAttemptUtc > DateTime.UtcNow.AddMinutes(-1));
        // No clean sweep happened, so success stays unstamped.
        Assert.Equal(DateTime.MinValue, state.LastSuccessUtc);
    }

    [Fact]
    public async Task A_sweep_whose_deletes_all_failed_is_not_recorded_as_a_success()
    {
        var jobState = new MockBackgroundJobStateRepository();
        var users = new Mock<IUserRepository>();
        users
            .Setup(r => r.GetPurgeCandidatesAsync(It.IsAny<DateTime>(), It.IsAny<int>()))
            .ReturnsAsync(
                new List<User>
                {
                    new()
                    {
                        UniqueId = "u1",
                        Emails = "u1@test",
                        Roles = new List<User.Role>(),
                        OwnedHomeIds = new List<Guid>(),
                    },
                }
            );
        users.Setup(r => r.DeleteAsync("u1")).ThrowsAsync(new InvalidOperationException("cosmos down"));

        var services = new ServiceCollection();
        services.AddSingleton(users.Object);
        services.AddSingleton<IAuditLogRepository>(new Mock<IAuditLogRepository>().Object);
        services.AddSingleton<IBackgroundJobStateRepository>(jobState);
        services.AddScoped<UserPurgeRunner>();
        services.AddSingleton<ILogger<UserPurgeRunner>>(NullLogger<UserPurgeRunner>.Instance);
        var provider = services.BuildServiceProvider();

        var service = new UserPurgeService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(Enabled()),
            NullLogger<UserPurgeService>.Instance
        );

        await service.RunIfDueAsync(CancellationToken.None);

        var state = await jobState.GetAsync(UserPurgeService.JobName);
        Assert.NotNull(state);
        Assert.True(state!.LastAttemptUtc > DateTime.UtcNow.AddMinutes(-1));
        Assert.Equal(DateTime.MinValue, state.LastSuccessUtc);
    }

    [Fact]
    public async Task A_dry_run_does_not_consume_the_pacing_interval()
    {
        // A dry run deletes nothing, so it has no blast radius to cap. Consuming the interval would make
        // the cut-over runbook's "verify with DryRun, then turn it off" step wait a full day for the
        // first real run, with no way to force one.
        var jobState = new MockBackgroundJobStateRepository();
        var options = Enabled();
        options.DryRun = true;
        var (service, users) = Create(options, jobState);

        await service.RunIfDueAsync(CancellationToken.None);

        users.Verify(r => r.GetPurgeCandidatesAsync(It.IsAny<DateTime>(), It.IsAny<int>()), Times.Once);
        // The live key is untouched, so a dry run never delays a real purge...
        Assert.Null(await jobState.GetAsync(UserPurgeService.JobName));
        // ...but the dry sweep still paces itself, so a recycling host does not re-scan Users every start.
        Assert.NotNull(await jobState.GetAsync(UserPurgeService.DryRunJobName));
    }

    [Fact]
    public async Task A_dry_run_is_paced_by_its_own_key_across_restarts()
    {
        var jobState = new MockBackgroundJobStateRepository();
        await jobState.UpsertAsync(
            new BackgroundJobState
            {
                JobName = UserPurgeService.DryRunJobName,
                LastAttemptUtc = DateTime.UtcNow.AddHours(-2),
            }
        );

        var options = Enabled();
        options.DryRun = true;
        var (service, users) = Create(options, jobState);

        var waited = await service.RunIfDueAsync(CancellationToken.None);

        users.Verify(r => r.GetPurgeCandidatesAsync(It.IsAny<DateTime>(), It.IsAny<int>()), Times.Never);
        Assert.InRange(waited, TimeSpan.FromHours(21.5), TimeSpan.FromHours(22.5));
    }

    [Fact]
    public async Task A_future_dated_success_stamp_does_not_latch_the_purge_off()
    {
        var jobState = new MockBackgroundJobStateRepository();
        await jobState.UpsertAsync(
            new BackgroundJobState
            {
                JobName = UserPurgeService.JobName,
                LastAttemptUtc = DateTime.UtcNow.AddYears(1),
                LastSuccessUtc = DateTime.UtcNow.AddYears(1),
            }
        );

        var (service, users) = Create(Enabled(), jobState);

        await service.RunIfDueAsync(CancellationToken.None);

        users.Verify(r => r.GetPurgeCandidatesAsync(It.IsAny<DateTime>(), It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public async Task A_dry_run_is_not_blocked_by_an_interval_a_live_run_consumed()
    {
        // The runbook tells the operator to verify with DryRun. If a live sweep earlier in the interval
        // could silence it, that verification step would show nothing for up to a day.
        var jobState = new MockBackgroundJobStateRepository();
        await jobState.UpsertAsync(
            new BackgroundJobState
            {
                JobName = UserPurgeService.JobName,
                LastAttemptUtc = DateTime.UtcNow.AddHours(-2),
                LastSuccessUtc = DateTime.UtcNow.AddHours(-2),
            }
        );

        var options = Enabled();
        options.DryRun = true;
        var (service, users) = Create(options, jobState);

        await service.RunIfDueAsync(CancellationToken.None);

        users.Verify(r => r.GetPurgeCandidatesAsync(It.IsAny<DateTime>(), It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public async Task A_future_dated_attempt_stamp_does_not_latch_the_purge_off()
    {
        // Clock skew or a hand-edited document makes every later comparison negative; without a guard the
        // job would silently never run again.
        var jobState = new MockBackgroundJobStateRepository();
        await jobState.UpsertAsync(
            new BackgroundJobState
            {
                JobName = UserPurgeService.JobName,
                LastAttemptUtc = DateTime.UtcNow.AddYears(1),
                LastSuccessUtc = DateTime.UtcNow.AddYears(1),
            }
        );

        var (service, users) = Create(Enabled(), jobState);

        await service.RunIfDueAsync(CancellationToken.None);

        users.Verify(r => r.GetPurgeCandidatesAsync(It.IsAny<DateTime>(), It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public async Task An_interval_beyond_the_Task_Delay_ceiling_is_not_silently_capped()
    {
        // IntervalHours is a comparison window, not a Task.Delay argument. Capping it at the ~49.7 day
        // delay ceiling would make a quarterly purge sweep roughly twice per configured period.
        var jobState = new MockBackgroundJobStateRepository();
        await jobState.UpsertAsync(
            new BackgroundJobState
            {
                JobName = UserPurgeService.JobName,
                LastAttemptUtc = DateTime.UtcNow.AddDays(-60),
                LastSuccessUtc = DateTime.UtcNow.AddDays(-60),
            }
        );

        var options = Enabled();
        options.IntervalHours = 2160; // 90 days

        var (service, users) = Create(options, jobState);

        var waited = await service.RunIfDueAsync(CancellationToken.None);

        users.Verify(r => r.GetPurgeCandidatesAsync(It.IsAny<DateTime>(), It.IsAny<int>()), Times.Never);
        Assert.InRange(waited, TimeSpan.FromDays(29), TimeSpan.FromDays(31));
    }

    [Fact]
    public async Task RunIfDue_observes_cancellation()
    {
        var jobState = new Mock<IBackgroundJobStateRepository>(MockBehavior.Strict);
        var (service, _) = Create(Enabled(), jobState.Object);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunIfDueAsync(cts.Token));
        jobState.Verify(r => r.GetAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Purges_and_stamps_state_once_the_interval_has_elapsed()
    {
        var jobState = new MockBackgroundJobStateRepository();
        await jobState.UpsertAsync(
            new BackgroundJobState
            {
                JobName = UserPurgeService.JobName,
                LastSuccessUtc = DateTime.UtcNow.AddHours(-25),
                LastAttemptUtc = DateTime.UtcNow.AddHours(-25),
            }
        );

        var (service, users) = Create(Enabled(), jobState);

        var waited = await service.RunIfDueAsync(CancellationToken.None);

        users.Verify(r => r.GetPurgeCandidatesAsync(It.IsAny<DateTime>(), It.IsAny<int>()), Times.Once);
        Assert.Equal(TimeSpan.FromHours(24), waited);

        var state = await jobState.GetAsync(UserPurgeService.JobName);
        Assert.NotNull(state);
        Assert.True(state!.LastSuccessUtc > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task Purges_when_state_has_never_been_written()
    {
        var jobState = new MockBackgroundJobStateRepository();
        var (service, users) = Create(Enabled(), jobState);

        await service.RunIfDueAsync(CancellationToken.None);

        users.Verify(r => r.GetPurgeCandidatesAsync(It.IsAny<DateTime>(), It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public async Task Does_not_run_when_disabled()
    {
        var ran = new TaskCompletionSource();
        var options = Enabled();
        options.Enabled = false;
        var (service, users) = Create(options, new MockBackgroundJobStateRepository(), ran);

        await service.StartAsync(CancellationToken.None);
        var completed = await Task.WhenAny(ran.Task, Task.Delay(TimeSpan.FromMilliseconds(250)));
        await service.StopAsync(CancellationToken.None);

        Assert.NotSame(ran.Task, completed);
        users.Verify(r => r.GetPurgeCandidatesAsync(It.IsAny<DateTime>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void DryRun_defaults_to_true_so_a_partial_config_cannot_delete()
    {
        // The deleted Function host supplied this fail-safe at its read site
        // (GetValue("UserPurge:DryRun", true)); binding the section onto the type must not lose it.
        Assert.True(new UserPurgeOptions().DryRun);
    }
}
