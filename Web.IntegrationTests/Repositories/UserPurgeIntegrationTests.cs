using Microsoft.Extensions.Logging.Abstractions;
using Web.IntegrationTests.Support;
using Web.Models;
using Web.Services;
using Web.Services.Repositories;

namespace Web.IntegrationTests.Repositories;

/// <summary>
/// Cosmos-backed checks for purge candidate queries, deletes, and <see cref="UserPurgeRunner"/>.
/// Requires RUN_COSMOS_INTEGRATION_TESTS=1 and a running emulator or test account (see README).
/// </summary>
[Collection("Cosmos integration")]
public sealed class UserPurgeIntegrationTests
{
    private readonly CosmosIntegrationFixture _fixture;

    public UserPurgeIntegrationTests(CosmosIntegrationFixture fixture) => _fixture = fixture;

    private static User NewUnassociatedUser(string uniqueId) =>
        new()
        {
            UniqueId = uniqueId,
            NameIdentifier = uniqueId.Replace("google.com", "", StringComparison.Ordinal),
            GivenName = "Purge",
            Surname = "Test",
            IdentityProvider = "google.com",
            Emails = "purge-test@example.com",
            StreetAddress = "1 Test St",
            Roles = new List<User.Role> { User.Role.Resident },
            OwnedHomeIds = new List<Guid>(),
            LastLoggedIn = DateTime.UtcNow
        };

    [SkippableFact]
    public async Task GetPurgeCandidatesAsync_includes_user_when_unassociated_before_cutoff()
    {
        Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

        var repo = _fixture.CreateUserRepository();
        var uniqueId = $"google.com{Guid.NewGuid():N}";
        var user = NewUnassociatedUser(uniqueId);

        await repo.UpsertAsync(user).ConfigureAwait(false);
        var loaded = await repo.GetByUniqueIdAsync(uniqueId).ConfigureAwait(false);
        Assert.NotNull(loaded);
        loaded.UnassociatedSinceUtc = DateTime.UtcNow.AddDays(-45);
        await repo.UpsertAsync(loaded).ConfigureAwait(false);

        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-30);
            var candidates = await repo.GetPurgeCandidatesAsync(cutoff, 100).ConfigureAwait(false);
            Assert.Contains(candidates, c => c.UniqueId == uniqueId);
        }
        finally
        {
            await repo.DeleteAsync(uniqueId).ConfigureAwait(false);
        }
    }

    [SkippableFact]
    public async Task GetPurgeCandidatesAsync_excludes_user_when_unassociated_too_recently()
    {
        Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

        var repo = _fixture.CreateUserRepository();
        var uniqueId = $"google.com{Guid.NewGuid():N}";
        var user = NewUnassociatedUser(uniqueId);

        await repo.UpsertAsync(user).ConfigureAwait(false);
        var loaded = await repo.GetByUniqueIdAsync(uniqueId).ConfigureAwait(false);
        Assert.NotNull(loaded);
        loaded.UnassociatedSinceUtc = DateTime.UtcNow.AddDays(-5);
        await repo.UpsertAsync(loaded).ConfigureAwait(false);

        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-30);
            var candidates = await repo.GetPurgeCandidatesAsync(cutoff, 100).ConfigureAwait(false);
            Assert.DoesNotContain(candidates, c => c.UniqueId == uniqueId);
        }
        finally
        {
            await repo.DeleteAsync(uniqueId).ConfigureAwait(false);
        }
    }

    [SkippableFact]
    public async Task GetPurgeCandidatesAsync_excludes_user_with_owned_homes()
    {
        Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

        var repo = _fixture.CreateUserRepository();
        var uniqueId = $"google.com{Guid.NewGuid():N}";
        var homeId = Guid.NewGuid();
        var user = NewUnassociatedUser(uniqueId);
        user.OwnedHomeIds = new List<Guid> { homeId };

        await repo.UpsertAsync(user).ConfigureAwait(false);

        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-30);
            var candidates = await repo.GetPurgeCandidatesAsync(cutoff, 100).ConfigureAwait(false);
            Assert.DoesNotContain(candidates, c => c.UniqueId == uniqueId);
        }
        finally
        {
            await repo.DeleteAsync(uniqueId).ConfigureAwait(false);
        }
    }

    [SkippableFact]
    public async Task GetPurgeCandidatesAsync_includes_user_when_no_roles_before_cutoff()
    {
        Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

        var repo = _fixture.CreateUserRepository();
        var uniqueId = $"google.com{Guid.NewGuid():N}";
        var user = NewUnassociatedUser(uniqueId);
        user.Roles = new List<User.Role>();

        await repo.UpsertAsync(user).ConfigureAwait(false);
        var loaded = await repo.GetByUniqueIdAsync(uniqueId).ConfigureAwait(false);
        Assert.NotNull(loaded);
        loaded.OwnedHomeIds = new List<Guid> { Guid.NewGuid() };
        loaded.NoRolesSinceUtc = DateTime.UtcNow.AddDays(-45);
        await repo.UpsertAsync(loaded).ConfigureAwait(false);

        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-30);
            var candidates = await repo.GetPurgeCandidatesAsync(cutoff, 100).ConfigureAwait(false);
            Assert.Contains(candidates, c => c.UniqueId == uniqueId);
        }
        finally
        {
            await repo.DeleteAsync(uniqueId).ConfigureAwait(false);
        }
    }

    [SkippableFact]
    public async Task GetPurgeCandidatesAsync_excludes_user_when_roles_present()
    {
        Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

        var repo = _fixture.CreateUserRepository();
        var uniqueId = $"google.com{Guid.NewGuid():N}";
        var user = NewUnassociatedUser(uniqueId);
        user.Roles = new List<User.Role>();

        await repo.UpsertAsync(user).ConfigureAwait(false);
        var loaded = await repo.GetByUniqueIdAsync(uniqueId).ConfigureAwait(false);
        Assert.NotNull(loaded);
        loaded.OwnedHomeIds = new List<Guid> { Guid.NewGuid() };
        loaded.NoRolesSinceUtc = DateTime.UtcNow.AddDays(-45);
        await repo.UpsertAsync(loaded).ConfigureAwait(false);
        loaded = await repo.GetByUniqueIdAsync(uniqueId).ConfigureAwait(false);
        Assert.NotNull(loaded);
        loaded.Roles = new List<User.Role> { User.Role.Resident };
        await repo.UpsertAsync(loaded).ConfigureAwait(false);

        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-30);
            var candidates = await repo.GetPurgeCandidatesAsync(cutoff, 100).ConfigureAwait(false);
            Assert.DoesNotContain(candidates, c => c.UniqueId == uniqueId);
        }
        finally
        {
            await repo.DeleteAsync(uniqueId).ConfigureAwait(false);
        }
    }

    [SkippableFact]
    public async Task DeleteAsync_removes_document_and_second_delete_does_not_throw()
    {
        Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

        var repo = _fixture.CreateUserRepository();
        var uniqueId = $"google.com{Guid.NewGuid():N}";
        await repo.UpsertAsync(NewUnassociatedUser(uniqueId)).ConfigureAwait(false);

        await repo.DeleteAsync(uniqueId).ConfigureAwait(false);
        var missing = await repo.GetByUniqueIdAsync(uniqueId).ConfigureAwait(false);
        Assert.Null(missing);

        await repo.DeleteAsync(uniqueId).ConfigureAwait(false);
    }

    [SkippableFact]
    public async Task UserPurgeRunner_dry_run_does_not_delete_eligible_user()
    {
        Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

        var userRepo = _fixture.CreateUserRepository();
        var auditRepo = _fixture.CreateAuditLogRepository();
        var uniqueId = $"google.com{Guid.NewGuid():N}";
        await userRepo.UpsertAsync(NewUnassociatedUser(uniqueId)).ConfigureAwait(false);
        var loaded = await userRepo.GetByUniqueIdAsync(uniqueId).ConfigureAwait(false);
        Assert.NotNull(loaded);
        loaded.UnassociatedSinceUtc = DateTime.UtcNow.AddDays(-40);
        await userRepo.UpsertAsync(loaded).ConfigureAwait(false);

        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-30);
            var pre = await userRepo.GetPurgeCandidatesAsync(cutoff, 200).ConfigureAwait(false);
            Skip.If(
                !pre.Exists(u => u.UniqueId == uniqueId),
                "Test user was not returned as a purge candidate; use a clean integration DB or increase max scan.");

            var runner = new UserPurgeRunner(
                userRepo,
                auditRepo,
                NullLogger<UserPurgeRunner>.Instance);

            var result = await runner.RunAsync(
                    new UserPurgeOptions
                    {
                        Enabled = true,
                        DryRun = true,
                        PurgeAfterDays = 30,
                        MaxDeletesPerRun = 200
                    })
                .ConfigureAwait(false);

            Assert.True(result.WouldDelete >= 1);
            Assert.Equal(0, result.Deleted);

            var stillThere = await userRepo.GetByUniqueIdAsync(uniqueId).ConfigureAwait(false);
            Assert.NotNull(stillThere);
        }
        finally
        {
            await userRepo.DeleteAsync(uniqueId).ConfigureAwait(false);
        }
    }

    [SkippableFact]
    public async Task UserPurgeRunner_skips_administrator_and_deletes_non_admin_when_enabled()
    {
        Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

        var userRepo = _fixture.CreateUserRepository();
        var auditRepo = _fixture.CreateAuditLogRepository();
        var adminId = $"google.comadmin{Guid.NewGuid():N}";
        var residentId = $"google.comres{Guid.NewGuid():N}";

        var adminUser = NewUnassociatedUser(adminId);
        adminUser.Roles = new List<User.Role> { User.Role.Administrator };
        var residentUser = NewUnassociatedUser(residentId);

        await userRepo.UpsertAsync(adminUser).ConfigureAwait(false);
        await userRepo.UpsertAsync(residentUser).ConfigureAwait(false);

        foreach (var id in new[] { adminId, residentId })
        {
            var u = await userRepo.GetByUniqueIdAsync(id).ConfigureAwait(false);
            Assert.NotNull(u);
            u.UnassociatedSinceUtc = DateTime.UtcNow.AddDays(-40);
            await userRepo.UpsertAsync(u).ConfigureAwait(false);
        }

        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-30);
            var pre = await userRepo.GetPurgeCandidatesAsync(cutoff, 200).ConfigureAwait(false);
            Skip.If(
                !pre.Exists(u => u.UniqueId == adminId) || !pre.Exists(u => u.UniqueId == residentId),
                "Both test users must appear in the purge candidate set; use a clean integration DB or increase max scan.");

            var runner = new UserPurgeRunner(
                userRepo,
                auditRepo,
                NullLogger<UserPurgeRunner>.Instance);

            var result = await runner.RunAsync(
                    new UserPurgeOptions
                    {
                        Enabled = true,
                        DryRun = false,
                        PurgeAfterDays = 30,
                        MaxDeletesPerRun = 200
                    })
                .ConfigureAwait(false);

            Assert.True(result.SkippedAdministrator >= 1);
            Assert.True(result.Deleted >= 1);

            var adminAfter = await userRepo.GetByUniqueIdAsync(adminId).ConfigureAwait(false);
            Assert.NotNull(adminAfter);
            Assert.Contains(User.Role.Administrator, adminAfter.Roles ?? new List<User.Role>());

            Assert.Null(await userRepo.GetByUniqueIdAsync(residentId).ConfigureAwait(false));
        }
        finally
        {
            await userRepo.DeleteAsync(adminId).ConfigureAwait(false);
            await userRepo.DeleteAsync(residentId).ConfigureAwait(false);
        }
    }
}
