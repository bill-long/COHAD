using System;
using System.Linq;
using System.Threading.Tasks;
using Web.MockData;
using Web.Models;
using Web.Services.Repositories;
using Xunit;

namespace Web.UnitTests;

public sealed class MockHomeRepositoryTests
{
    // MockHomeRepository seeds two homes; SampleHomeId is the primary mock home.
    private static readonly Guid SeededHomeId = MockDataConstants.SampleHomeId;

    [Fact]
    public async Task GetAllAsync_populates_ETag()
    {
        var repo = new MockHomeRepository();

        var homes = await repo.GetAllAsync();

        Assert.NotEmpty(homes);
        Assert.All(homes, h => Assert.False(string.IsNullOrEmpty(h.ETag)));
    }

    [Fact]
    public async Task GetByIdsAsync_populates_ETag()
    {
        var repo = new MockHomeRepository();

        var homes = await repo.GetByIdsAsync(new() { SeededHomeId });

        var home = Assert.Single(homes);
        Assert.False(string.IsNullOrEmpty(home.ETag));
    }

    [Fact]
    public async Task GetByEmailAsync_populates_ETag()
    {
        var repo = new MockHomeRepository();

        // The primary seeded home has this directory email.
        var homes = await repo.GetByEmailAsync("home@cohad.local");

        var home = Assert.Single(homes);
        Assert.False(string.IsNullOrEmpty(home.ETag));
    }

    [Fact]
    public async Task Read_via_query_path_then_stale_upsert_conflicts()
    {
        // A read-modify-write through a non-GetById query path must enforce optimistic concurrency,
        // matching CosmosHomeRepository. Before the fix, GetByEmailAsync returned a null ETag, so the
        // stale write below would silently succeed under Mock while failing under Cosmos.
        var repo = new MockHomeRepository();

        var stale = (await repo.GetByEmailAsync("home@cohad.local")).Single();
        Assert.False(string.IsNullOrEmpty(stale.ETag));

        // Another writer advances the version.
        var current = await repo.GetByIdAsync(SeededHomeId);
        current.StreetName = "Changed Lane";
        await repo.UpsertAsync(current);

        // The stale copy (older ETag) must now be rejected.
        stale.StreetName = "Conflicting Lane";
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => repo.UpsertAsync(stale));
    }
}
