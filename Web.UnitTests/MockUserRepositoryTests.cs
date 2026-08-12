using System;
using System.Linq;
using System.Threading.Tasks;
using Web.MockData;
using Web.Models;
using Web.Services.Repositories;
using Xunit;

namespace Web.UnitTests;

public sealed class MockUserRepositoryTests
{
    // MockUserRepository seeds two users; AdminUniqueId is the primary mock user.
    private static readonly string SeededUserId = MockDataConstants.AdminUniqueId;

    [Fact]
    public async Task GetAllAsync_populates_ETag()
    {
        var repo = new MockUserRepository();

        var users = await repo.GetAllAsync();

        Assert.NotEmpty(users);
        Assert.All(users, u => Assert.False(string.IsNullOrEmpty(u.ETag)));
    }

    [Fact]
    public async Task GetByUniqueIdAsync_populates_ETag()
    {
        var repo = new MockUserRepository();

        var user = await repo.GetByUniqueIdAsync(SeededUserId);

        Assert.NotNull(user);
        Assert.False(string.IsNullOrEmpty(user.ETag));
    }

    [Fact]
    public async Task GetPurgeCandidatesAsync_populates_ETag()
    {
        var repo = new MockUserRepository();
        var user = await repo.GetByUniqueIdAsync(SeededUserId);
        user.Roles = new();
        user.OwnedHomeIds = new();
        // UpsertAsync applies UserAssociationState, which stamps the no-role/no-home clocks.
        await repo.UpsertAsync(user);

        var candidates = await repo.GetPurgeCandidatesAsync(DateTime.UtcNow.AddDays(1));

        var candidate = Assert.Single(candidates, c => c.UniqueId == SeededUserId);
        Assert.False(string.IsNullOrEmpty(candidate.ETag));
    }

    [Fact]
    public async Task UpsertAsync_updates_input_ETag_in_place_and_returns_same_instance()
    {
        var repo = new MockUserRepository();
        var user = await repo.GetByUniqueIdAsync(SeededUserId);
        var originalETag = user.ETag;
        user.GivenName = "Renamed";

        var saved = await repo.UpsertAsync(user);

        // Parity with CosmosUserRepository.UpsertAsync, which mutates the caller's instance ETag in
        // place and returns that same object. A caller reusing the User across sequential upserts must
        // see the fresh ETag without recapturing the return value, or Mock would throw a spurious 409
        // that Cosmos never would.
        Assert.False(string.IsNullOrEmpty(saved.ETag));
        Assert.Same(user, saved);
        Assert.Equal(saved.ETag, user.ETag);
        Assert.NotEqual(originalETag, user.ETag);
    }

    [Fact]
    public async Task UpsertAsync_allows_reusing_same_instance_across_sequential_writes()
    {
        // The concrete consequence of in-place ETag parity: a caller can upsert the same User twice in
        // a row without recapturing it and must not hit a concurrency conflict (Cosmos permits this).
        var repo = new MockUserRepository();
        var user = await repo.GetByUniqueIdAsync(SeededUserId);

        user.GivenName = "First Rename";
        await repo.UpsertAsync(user);

        user.GivenName = "Second Rename";
        await repo.UpsertAsync(user); // must not throw ConcurrencyConflictException

        var reread = await repo.GetByUniqueIdAsync(SeededUserId);
        Assert.Equal("Second Rename", reread.GivenName);
    }

    [Fact]
    public async Task UpsertAsync_with_stale_ETag_throws_ConcurrencyConflict()
    {
        var repo = new MockUserRepository();

        var stale = await repo.GetByUniqueIdAsync(SeededUserId);
        Assert.False(string.IsNullOrEmpty(stale.ETag));

        // Another writer advances the version.
        var current = await repo.GetByUniqueIdAsync(SeededUserId);
        current.GivenName = "Changed";
        await repo.UpsertAsync(current);

        // The stale copy (older ETag) must now be rejected.
        stale.GivenName = "Conflicting";
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => repo.UpsertAsync(stale));
    }

    [Fact]
    public async Task UpsertAsync_without_ETag_writes_blind()
    {
        // Null/empty ETag is a blind write, matching Cosmos (no If-Match header is sent). This is the
        // create path and keeps callers that construct a User without reading working.
        var repo = new MockUserRepository();

        var newUser = new User { UniqueId = "google.comnew", GivenName = "New" };
        var saved = await repo.UpsertAsync(newUser);
        Assert.False(string.IsNullOrEmpty(saved.ETag));

        var overwrite = new User { UniqueId = SeededUserId, GivenName = "Blind Overwrite" };
        await repo.UpsertAsync(overwrite); // must not throw despite the version having advanced

        var reread = await repo.GetByUniqueIdAsync(SeededUserId);
        Assert.Equal("Blind Overwrite", reread.GivenName);
    }

    [Fact]
    public async Task UpsertAsync_with_ETag_conflicts_after_the_user_was_deleted()
    {
        // A caller-supplied ETag asserts "this write continues from a document I read". Once that
        // document is deleted, the write must conflict rather than silently resurrect the user -
        // matching CosmosUserRepository, which replaces (not upserts) when an ETag is present.
        var repo = new MockUserRepository();
        var stale = await repo.GetByUniqueIdAsync(SeededUserId);
        Assert.False(string.IsNullOrEmpty(stale.ETag));

        await repo.DeleteAsync(SeededUserId);

        stale.GivenName = "Resurrected";
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => repo.UpsertAsync(stale));
        Assert.Null(await repo.GetByUniqueIdAsync(SeededUserId));
    }

    [Fact]
    public async Task Stale_pre_delete_ETag_never_matches_a_recreated_user()
    {
        // Mock versions come from a monotonic counter shared across all keys, so delete-and-recreate
        // cannot reissue an old ETag: a stale pre-delete snapshot must conflict instead of silently
        // overwriting the recreated account, matching Cosmos, where ETags never recur.
        var repo = new MockUserRepository();
        var stale = await repo.GetByUniqueIdAsync(SeededUserId);

        await repo.DeleteAsync(SeededUserId);
        await repo.UpsertAsync(new User { UniqueId = SeededUserId, GivenName = "Recreated" });

        stale.GivenName = "Conflicting";
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => repo.UpsertAsync(stale));

        var reread = await repo.GetByUniqueIdAsync(SeededUserId);
        Assert.Equal("Recreated", reread.GivenName);
    }
}
