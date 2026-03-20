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
            OwnedHomeIds = new List<Guid>()
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
        users.Verify(
            r => r.GetPurgeCandidatesAsync(It.IsAny<DateTime>(), It.IsAny<int>()),
            Times.Never);
    }

    [Fact]
    public async Task RunAsync_dry_run_does_not_delete_or_audit()
    {
        var list = new List<User> { MakeUser("u1"), MakeUser("u2") };
        var users = new Mock<IUserRepository>();
        users
            .Setup(r => r.GetPurgeCandidatesAsync(It.IsAny<DateTime>(), It.IsAny<int>()))
            .ReturnsAsync(list);
        var audit = new Mock<IAuditLogRepository>(MockBehavior.Strict);
        var runner = new UserPurgeRunner(users.Object, audit.Object, NullLogger<UserPurgeRunner>.Instance);

        var result = await runner.RunAsync(
            new UserPurgeOptions { Enabled = true, DryRun = true, PurgeAfterDays = 30, MaxDeletesPerRun = 50 },
            CancellationToken.None);

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
        users
            .Setup(r => r.GetPurgeCandidatesAsync(It.IsAny<DateTime>(), It.IsAny<int>()))
            .ReturnsAsync(list);
        var audit = new Mock<IAuditLogRepository>();
        var runner = new UserPurgeRunner(users.Object, audit.Object, NullLogger<UserPurgeRunner>.Instance);

        var result = await runner.RunAsync(
            new UserPurgeOptions { Enabled = true, DryRun = false, PurgeAfterDays = 14, MaxDeletesPerRun = 10 },
            CancellationToken.None);

        Assert.Equal(2, result.CandidatesFound);
        Assert.Equal(1, result.SkippedAdministrator);
        Assert.Equal(1, result.Deleted);
        users.Verify(r => r.DeleteAsync("resident"), Times.Once);
        users.Verify(r => r.DeleteAsync("admin"), Times.Never);
        audit.Verify(a => a.AddAsync(It.Is<NewAuditLogEntry>(e => e.SubjectId == "resident")), Times.Once);
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
            .Callback<DateTime, int>((c, m) =>
            {
                capturedCutoff = c;
                capturedMax = m;
            })
            .ReturnsAsync(new List<User>());

        await runner.RunAsync(
            new UserPurgeOptions { Enabled = true, DryRun = true, PurgeAfterDays = 7, MaxDeletesPerRun = 42 },
            CancellationToken.None);

        Assert.NotNull(capturedCutoff);
        Assert.Equal(42, capturedMax);
        var expectedLatest = DateTime.UtcNow.AddDays(-7);
        var expectedEarliest = DateTime.UtcNow.AddDays(-7).AddMinutes(-1);
        Assert.True(capturedCutoff.Value <= expectedLatest);
        Assert.True(capturedCutoff.Value >= expectedEarliest.AddMinutes(-1));
    }
}
