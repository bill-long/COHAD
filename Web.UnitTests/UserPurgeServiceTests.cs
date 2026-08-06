using System;
using System.Collections.Generic;
using System.Linq;
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
        TaskCompletionSource? ranSignal = null
    )
    {
        var users = new Mock<IUserRepository>();
        users
            .Setup(r => r.GetPurgeCandidatesAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(new List<User>())
            .Callback(() => ranSignal?.TrySetResult());

        var services = new ServiceCollection();
        services.AddSingleton(users.Object);
        services.AddSingleton<IAuditLogRepository>(new Mock<IAuditLogRepository>().Object);
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
        // The invariant: the loop runs before it waits. If it delayed first, a deploy cadence shorter than
        // IntervalHours would silently mean the purge never runs at all. Running more often is free: the
        // sweep selects against a rolling cutoff and deleted users stop matching.
        var ran = new TaskCompletionSource();
        var (service, users) = Create(Enabled(), ran);

        await service.StartAsync(CancellationToken.None);
        var completed = await Task.WhenAny(ran.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        await service.StopAsync(CancellationToken.None);

        Assert.Same(ran.Task, completed);
        users.Verify(r => r.GetPurgeCandidatesAsync(It.IsAny<DateTime>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task Does_not_run_when_disabled()
    {
        var ran = new TaskCompletionSource();
        var options = Enabled();
        options.Enabled = false;
        var (service, users) = Create(options, ran);

        await service.StartAsync(CancellationToken.None);
        var completed = await Task.WhenAny(ran.Task, Task.Delay(TimeSpan.FromMilliseconds(250)));
        await service.StopAsync(CancellationToken.None);

        Assert.NotSame(ran.Task, completed);
        users.Verify(r => r.GetPurgeCandidatesAsync(It.IsAny<DateTime>()), Times.Never);
    }

    [Fact]
    public void DryRun_defaults_to_true_so_a_partial_config_cannot_delete()
    {
        // The deleted Function host supplied this fail-safe at its read site
        // (GetValue("UserPurge:DryRun", true)); binding the section onto the type must not lose it, and
        // appsettings.json deliberately does not restate it.
        Assert.True(new UserPurgeOptions().DryRun);
    }

    [Fact]
    public void The_purge_takes_no_dependency_on_durable_job_state()
    {
        // Locks the simplification: the purge is unbounded, so "how often did this run" is not
        // load-bearing and BackgroundJobState is the PayPal sync's concern alone. A future change that
        // reintroduces a per-run cap must also reintroduce the pacing state, deliberately.
        var ctorParams = typeof(UserPurgeService).GetConstructors()[0].GetParameters();
        Assert.DoesNotContain(ctorParams, p => p.ParameterType == typeof(IBackgroundJobStateRepository));

        // Asserted as an exact set rather than a name pattern: a cap called DeleteLimit or BatchSize is
        // the same coupling, and would slip past a substring check for "Max".
        var optionNames = typeof(UserPurgeOptions).GetProperties().Select(p => p.Name).OrderBy(n => n);
        Assert.Equal(
            new[] { "DryRun", "Enabled", "IntervalHours", "PurgeAfterDays", "StartupDelaySeconds" },
            optionNames
        );
    }

    [Fact]
    public async Task The_mock_repository_returns_the_same_candidates_the_cosmos_query_would()
    {
        // Mock/Cosmos parity: the purge now ships inside the web app, so MockData must be able to produce
        // a candidate at all. This previously returned an empty list unconditionally.
        var repo = new MockUserRepository();
        var cutoff = DateTime.UtcNow.AddDays(-30);

        await repo.UpsertAsync(
            new User
            {
                UniqueId = "stale",
                Emails = "stale@test",
                Roles = new List<User.Role> { User.Role.Resident },
                OwnedHomeIds = new List<Guid>(),
                UnassociatedSinceUtc = DateTime.UtcNow.AddDays(-45),
            }
        );
        await repo.UpsertAsync(
            new User
            {
                UniqueId = "recent",
                Emails = "recent@test",
                Roles = new List<User.Role> { User.Role.Resident },
                OwnedHomeIds = new List<Guid>(),
                UnassociatedSinceUtc = DateTime.UtcNow.AddDays(-5),
            }
        );
        await repo.UpsertAsync(
            new User
            {
                UniqueId = "owner",
                Emails = "owner@test",
                Roles = new List<User.Role> { User.Role.Resident },
                OwnedHomeIds = new List<Guid> { Guid.NewGuid() },
                UnassociatedSinceUtc = DateTime.UtcNow.AddDays(-45),
            }
        );

        var candidates = await repo.GetPurgeCandidatesAsync(cutoff);

        Assert.Contains(candidates, c => c.UniqueId == "stale");
        Assert.DoesNotContain(candidates, c => c.UniqueId == "recent");
        Assert.DoesNotContain(candidates, c => c.UniqueId == "owner");
    }
}
