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
        users.Verify(r => r.GetPurgeCandidatesAsync(It.IsAny<DateTime>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_dry_run_does_not_delete_or_audit()
    {
        var list = new List<User> { MakeUser("u1"), MakeUser("u2") };
        var users = new Mock<IUserRepository>();
        users.Setup(r => r.GetPurgeCandidatesAsync(It.IsAny<DateTime>(), It.IsAny<int>())).ReturnsAsync(list);
        var audit = new Mock<IAuditLogRepository>(MockBehavior.Strict);
        var runner = new UserPurgeRunner(users.Object, audit.Object, NullLogger<UserPurgeRunner>.Instance);

        var result = await runner.RunAsync(
            new UserPurgeOptions
            {
                Enabled = true,
                DryRun = true,
                PurgeAfterDays = 30,
                MaxDeletesPerRun = 50,
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
        users.Setup(r => r.GetPurgeCandidatesAsync(It.IsAny<DateTime>(), It.IsAny<int>())).ReturnsAsync(list);
        var audit = new Mock<IAuditLogRepository>();
        var runner = new UserPurgeRunner(users.Object, audit.Object, NullLogger<UserPurgeRunner>.Instance);

        var result = await runner.RunAsync(
            new UserPurgeOptions
            {
                Enabled = true,
                DryRun = false,
                PurgeAfterDays = 14,
                MaxDeletesPerRun = 10,
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
    public async Task RunAsync_passes_cutoff_and_max_to_repository()
    {
        var users = new Mock<IUserRepository>();
        users
            .Setup(r => r.GetPurgeCandidatesAsync(It.IsAny<DateTime>(), It.IsAny<int>()))
            .ReturnsAsync(new List<User>());
        var audit = new Mock<IAuditLogRepository>(MockBehavior.Strict);
        var runner = new UserPurgeRunner(users.Object, audit.Object, NullLogger<UserPurgeRunner>.Instance);

        DateTime? capturedCutoff = null;
        int? capturedMax = null;
        users
            .Setup(r => r.GetPurgeCandidatesAsync(It.IsAny<DateTime>(), It.IsAny<int>()))
            .Callback<DateTime, int>(
                (c, m) =>
                {
                    capturedCutoff = c;
                    capturedMax = m;
                }
            )
            .ReturnsAsync(new List<User>());

        await runner.RunAsync(
            new UserPurgeOptions
            {
                Enabled = true,
                DryRun = true,
                PurgeAfterDays = 7,
                MaxDeletesPerRun = 42,
            },
            CancellationToken.None
        );

        Assert.NotNull(capturedCutoff);
        Assert.Equal(42, capturedMax);
        var expectedLatest = DateTime.UtcNow.AddDays(-7);
        var expectedEarliest = DateTime.UtcNow.AddDays(-7).AddMinutes(-1);
        Assert.True(capturedCutoff.Value <= expectedLatest);
        Assert.True(capturedCutoff.Value >= expectedEarliest.AddMinutes(-1));
    }

    private static UserPurgeOptions LiveOptions() =>
        new()
        {
            Enabled = true,
            DryRun = false,
            PurgeAfterDays = 30,
            MaxDeletesPerRun = 100,
        };

    [Fact]
    public async Task RunAsync_does_not_delete_when_the_audit_write_fails()
    {
        // The invariant: no account is ever hard-deleted without a durable record of it. The delete is
        // irreversible; a failed audit merely leaves the user in place for the next sweep.
        var users = new Mock<IUserRepository>(MockBehavior.Strict);
        users
            .Setup(r => r.GetPurgeCandidatesAsync(It.IsAny<DateTime>(), It.IsAny<int>()))
            .ReturnsAsync(new List<User> { MakeUser("u1") });

        var audit = new Mock<IAuditLogRepository>();
        audit.Setup(a => a.AddAsync(It.IsAny<NewAuditLogEntry>())).ThrowsAsync(new InvalidOperationException("429"));

        var runner = new UserPurgeRunner(users.Object, audit.Object, NullLogger<UserPurgeRunner>.Instance);

        var result = await runner.RunAsync(LiveOptions(), CancellationToken.None);

        users.Verify(r => r.DeleteAsync(It.IsAny<string>()), Times.Never);
        Assert.Equal(0, result.Deleted);
        Assert.Equal(1, result.Errors);
    }

    [Fact]
    public async Task RunAsync_writes_a_compensating_entry_when_the_delete_fails_after_auditing()
    {
        // The audit entry is written first, so a failed delete leaves the log asserting a deletion that
        // did not happen. That claim must be corrected rather than left standing.
        var users = new Mock<IUserRepository>();
        users
            .Setup(r => r.GetPurgeCandidatesAsync(It.IsAny<DateTime>(), It.IsAny<int>()))
            .ReturnsAsync(new List<User> { MakeUser("u1") });
        users.Setup(r => r.DeleteAsync("u1")).ThrowsAsync(new InvalidOperationException("cosmos down"));

        var entries = new List<NewAuditLogEntry>();
        var audit = new Mock<IAuditLogRepository>();
        audit
            .Setup(a => a.AddAsync(It.IsAny<NewAuditLogEntry>()))
            .Callback<NewAuditLogEntry>(entries.Add)
            .Returns(Task.CompletedTask);

        var runner = new UserPurgeRunner(users.Object, audit.Object, NullLogger<UserPurgeRunner>.Instance);

        var result = await runner.RunAsync(LiveOptions(), CancellationToken.None);

        Assert.Equal(0, result.Deleted);
        Assert.Equal(1, result.Errors);
        Assert.Equal(2, entries.Count);
        Assert.Equal("u1", entries[1].SubjectId);
        // The entry must record uncertainty, not deny the deletion: a write that throws may still have
        // committed, and a deleted user is never a candidate again so nothing would ever correct a denial.
        Assert.Contains("may or", entries[1].Action);
        Assert.Contains("did not complete cleanly", entries[1].Action);
    }

    [Fact]
    public async Task RunAsync_aborts_the_sweep_after_repeated_delete_failures()
    {
        // Deletes are audited before they are attempted, so a systematically failing delete would write
        // two audit entries per candidate for the whole batch - hundreds of rows describing purges that
        // never happened, repeated every sweep. The breaker bounds that.
        var candidates = new List<User>();
        for (var i = 0; i < 20; i++)
        {
            candidates.Add(MakeUser($"u{i}"));
        }

        var users = new Mock<IUserRepository>();
        users.Setup(r => r.GetPurgeCandidatesAsync(It.IsAny<DateTime>(), It.IsAny<int>())).ReturnsAsync(candidates);
        users.Setup(r => r.DeleteAsync(It.IsAny<string>())).ThrowsAsync(new InvalidOperationException("429"));

        var audit = new Mock<IAuditLogRepository>();
        var entries = new List<NewAuditLogEntry>();
        audit
            .Setup(a => a.AddAsync(It.IsAny<NewAuditLogEntry>()))
            .Callback<NewAuditLogEntry>(entries.Add)
            .Returns(Task.CompletedTask);

        var runner = new UserPurgeRunner(users.Object, audit.Object, NullLogger<UserPurgeRunner>.Instance);

        var result = await runner.RunAsync(LiveOptions(), CancellationToken.None);

        users.Verify(
            r => r.DeleteAsync(It.IsAny<string>()),
            Times.Exactly(UserPurgeRunner.MaxConsecutiveDeleteFailures)
        );
        Assert.Equal(UserPurgeRunner.MaxConsecutiveDeleteFailures, result.Errors);
        Assert.Equal(0, result.Deleted);
        // Two entries per attempted candidate (the purge claim plus its correction), and nothing beyond.
        Assert.Equal(UserPurgeRunner.MaxConsecutiveDeleteFailures * 2, entries.Count);
    }

    [Fact]
    public async Task RunAsync_resets_the_failure_breaker_after_a_successful_delete()
    {
        var users = new Mock<IUserRepository>();
        users
            .Setup(r => r.GetPurgeCandidatesAsync(It.IsAny<DateTime>(), It.IsAny<int>()))
            .ReturnsAsync(new List<User> { MakeUser("bad1"), MakeUser("ok"), MakeUser("bad2") });
        users.Setup(r => r.DeleteAsync(It.Is<string>(id => id.StartsWith("bad"))))
            .ThrowsAsync(new InvalidOperationException("429"));
        users.Setup(r => r.DeleteAsync("ok")).Returns(Task.CompletedTask);

        var audit = new Mock<IAuditLogRepository>();
        audit.Setup(a => a.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var runner = new UserPurgeRunner(users.Object, audit.Object, NullLogger<UserPurgeRunner>.Instance);

        var result = await runner.RunAsync(LiveOptions(), CancellationToken.None);

        // Interleaved failures never reach the consecutive threshold, so the whole batch is attempted.
        Assert.Equal(1, result.Deleted);
        Assert.Equal(2, result.Errors);
        users.Verify(r => r.DeleteAsync(It.IsAny<string>()), Times.Exactly(3));
    }

    [Fact]
    public async Task RunAsync_audits_once_and_counts_no_error_on_the_happy_path()
    {
        var users = new Mock<IUserRepository>();
        users
            .Setup(r => r.GetPurgeCandidatesAsync(It.IsAny<DateTime>(), It.IsAny<int>()))
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
        Assert.DoesNotContain("FAILED", entries[0].Action);
    }
}
