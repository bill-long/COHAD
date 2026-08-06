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
