using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Web.Models;
using Web.Services;
using Web.Services.Repositories;
using Xunit;

namespace Web.UnitTests;

public sealed class UserPurgeRunnerTests
{
    private static User MakeUser(string uniqueId, bool administrator = false)
    {
        var roles = new List<User.Role>();
        if (administrator)
        {
            roles.Add(User.Role.Administrator);
        }

        return new User
        {
            UniqueId = uniqueId,
            Emails = $"{uniqueId}@test",
            Roles = roles,
            OwnedHomeIds = new List<Guid>(),
        };
    }

    [Fact]
    public async Task RunAsync_when_disabled_does_not_query_candidates()
    {
        var users = new Mock<IUserRepository>(MockBehavior.Strict);
        var audit = new Mock<IAuditLogRepository>(MockBehavior.Strict);
        var runner = new UserPurgeRunner(users.Object, audit.Object, NullLogger<UserPurgeRunner>.Instance);

        var result = await runner.RunAsync(new UserPurgeOptions { Enabled = false }, CancellationToken.None);

        Assert.Equal(0, result.CandidatesFound);
        users.Verify(r => r.GetPurgeCandidatesAsync(It.IsAny<DateTime>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_dry_run_does_not_delete_or_audit()
    {
        var list = new List<User> { MakeUser("u1"), MakeUser("u2") };
        var users = new Mock<IUserRepository>();
        users.Setup(r => r.GetPurgeCandidatesAsync(It.IsAny<DateTime>())).ReturnsAsync(list);
        var audit = new Mock<IAuditLogRepository>(MockBehavior.Strict);
        var runner = new UserPurgeRunner(users.Object, audit.Object, NullLogger<UserPurgeRunner>.Instance);

        var result = await runner.RunAsync(
            new UserPurgeOptions
            {
                Enabled = true,
                DryRun = true,
                PurgeAfterDays = 30,
            },
            CancellationToken.None
        );

        Assert.Equal(2, result.CandidatesFound);
        Assert.Equal(2, result.WouldDelete);
        Assert.Equal(0, result.Deleted);
        users.Verify(r => r.DeleteAsync(It.IsAny<string>()), Times.Never);
        audit.Verify(a => a.AddAsync(It.IsAny<NewAuditLogEntry>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_skips_administrator()
    {
        var list = new List<User> { MakeUser("admin", administrator: true), MakeUser("resident") };
        var users = new Mock<IUserRepository>();
        users.Setup(r => r.GetPurgeCandidatesAsync(It.IsAny<DateTime>())).ReturnsAsync(list);
        var audit = new Mock<IAuditLogRepository>();
        var runner = new UserPurgeRunner(users.Object, audit.Object, NullLogger<UserPurgeRunner>.Instance);

        var result = await runner.RunAsync(
            new UserPurgeOptions
            {
                Enabled = true,
                DryRun = false,
                PurgeAfterDays = 14,
            },
            CancellationToken.None
        );

        Assert.Equal(2, result.CandidatesFound);
        Assert.Equal(1, result.SkippedAdministrator);
        Assert.Equal(1, result.Deleted);
        users.Verify(r => r.DeleteAsync("resident"), Times.Once);
        users.Verify(r => r.DeleteAsync("admin"), Times.Never);
        audit.Verify(
            a =>
                a.AddAsync(
                    It.Is<NewAuditLogEntry>(e =>
                        e.SubjectId == "resident"
                        && e.Action.Contains("no homes or no roles", StringComparison.OrdinalIgnoreCase)
                    )
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task RunAsync_passes_the_cutoff_to_the_repository()
    {
        var users = new Mock<IUserRepository>();
        var audit = new Mock<IAuditLogRepository>(MockBehavior.Strict);
        var runner = new UserPurgeRunner(users.Object, audit.Object, NullLogger<UserPurgeRunner>.Instance);

        DateTime? capturedCutoff = null;
        users
            .Setup(r => r.GetPurgeCandidatesAsync(It.IsAny<DateTime>()))
            .Callback<DateTime>(c => capturedCutoff = c)
            .ReturnsAsync(new List<User>());

        await runner.RunAsync(
            new UserPurgeOptions
            {
                Enabled = true,
                DryRun = true,
                PurgeAfterDays = 7,
            },
            CancellationToken.None
        );

        Assert.NotNull(capturedCutoff);
        var expectedLatest = DateTime.UtcNow.AddDays(-7);
        var expectedEarliest = DateTime.UtcNow.AddDays(-7).AddMinutes(-1);
        Assert.True(capturedCutoff!.Value <= expectedLatest);
        Assert.True(capturedCutoff.Value >= expectedEarliest.AddMinutes(-1));
    }

    private static UserPurgeOptions LiveOptions() =>
        new()
        {
            Enabled = true,
            DryRun = false,
            PurgeAfterDays = 30,
        };

    [Fact]
    public async Task RunAsync_writes_no_audit_entry_when_the_delete_fails()
    {
        // The delete runs first, so the audit log only ever describes deletions that actually happened.
        // A failed delete must leave no trace claiming otherwise - a false entry cannot be distinguished
        // from a real one by reading the log, whereas the user simply remains a candidate next sweep.
        var users = new Mock<IUserRepository>();
        users
            .Setup(r => r.GetPurgeCandidatesAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(new List<User> { MakeUser("u1") });
        users.Setup(r => r.DeleteAsync("u1")).ThrowsAsync(new InvalidOperationException("cosmos down"));

        var audit = new Mock<IAuditLogRepository>(MockBehavior.Strict);
        var runner = new UserPurgeRunner(users.Object, audit.Object, NullLogger<UserPurgeRunner>.Instance);

        var result = await runner.RunAsync(LiveOptions(), CancellationToken.None);

        audit.Verify(a => a.AddAsync(It.IsAny<NewAuditLogEntry>()), Times.Never);
        Assert.Equal(0, result.Deleted);
        Assert.Equal(1, result.Errors);
    }

    [Fact]
    public async Task RunAsync_counts_an_error_when_the_audit_write_fails_after_a_delete()
    {
        // The account is gone, so Deleted stays counted - but the sweep is not clean, and the error count
        // is the only aggregate the job emits. Reporting errors=0 here would read as a healthy run while
        // the deletion exists only in the error log.
        var users = new Mock<IUserRepository>();
        users
            .Setup(r => r.GetPurgeCandidatesAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(new List<User> { MakeUser("u1") });
        users.Setup(r => r.DeleteAsync("u1")).Returns(Task.CompletedTask);

        var audit = new Mock<IAuditLogRepository>();
        audit.Setup(a => a.AddAsync(It.IsAny<NewAuditLogEntry>())).ThrowsAsync(new InvalidOperationException("429"));

        var runner = new UserPurgeRunner(users.Object, audit.Object, NullLogger<UserPurgeRunner>.Instance);

        var result = await runner.RunAsync(LiveOptions(), CancellationToken.None);

        Assert.Equal(1, result.Deleted);
        Assert.Equal(1, result.Errors);
    }

    [Fact]
    public async Task RunAsync_attempts_every_candidate_even_when_deletes_keep_failing()
    {
        // There is no per-run cap and no early abort: the sweep is unbounded by design.
        var candidates = new List<User>();
        for (var i = 0; i < 20; i++)
        {
            candidates.Add(MakeUser($"u{i}"));
        }

        var users = new Mock<IUserRepository>();
        users.Setup(r => r.GetPurgeCandidatesAsync(It.IsAny<DateTime>())).ReturnsAsync(candidates);
        users.Setup(r => r.DeleteAsync(It.IsAny<string>())).ThrowsAsync(new InvalidOperationException("429"));

        var audit = new Mock<IAuditLogRepository>(MockBehavior.Strict);
        var runner = new UserPurgeRunner(users.Object, audit.Object, NullLogger<UserPurgeRunner>.Instance);

        var result = await runner.RunAsync(LiveOptions(), CancellationToken.None);

        users.Verify(r => r.DeleteAsync(It.IsAny<string>()), Times.Exactly(20));
        Assert.Equal(20, result.Errors);
        Assert.Equal(0, result.Deleted);
        audit.Verify(a => a.AddAsync(It.IsAny<NewAuditLogEntry>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_audits_once_and_counts_no_error_on_the_happy_path()
    {
        var users = new Mock<IUserRepository>();
        users
            .Setup(r => r.GetPurgeCandidatesAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(new List<User> { MakeUser("u1") });
        users.Setup(r => r.DeleteAsync("u1")).Returns(Task.CompletedTask);

        var entries = new List<NewAuditLogEntry>();
        var audit = new Mock<IAuditLogRepository>();
        audit
            .Setup(a => a.AddAsync(It.IsAny<NewAuditLogEntry>()))
            .Callback<NewAuditLogEntry>(entries.Add)
            .Returns(Task.CompletedTask);

        var runner = new UserPurgeRunner(users.Object, audit.Object, NullLogger<UserPurgeRunner>.Instance);

        var result = await runner.RunAsync(LiveOptions(), CancellationToken.None);

        Assert.Equal(1, result.Deleted);
        Assert.Equal(0, result.Errors);
        Assert.Single(entries);
        Assert.Equal("u1", entries[0].SubjectId);
        Assert.Contains("30+ days", entries[0].Action);
    }
}
