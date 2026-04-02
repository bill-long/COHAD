using Web.IntegrationTests.Support;
using Web.Models;

namespace Web.IntegrationTests.Repositories;

[Collection("Cosmos integration")]
public sealed class HomeRepositoryIntegrationTests
{
    private readonly CosmosIntegrationFixture _fixture;

    public HomeRepositoryIntegrationTests(CosmosIntegrationFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Upsert_and_GetById_round_trips_without_partition_key_errors()
    {
        Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

        var repo = _fixture.CreateHomeRepository();
        var id = Guid.NewGuid();
        var home = new Home
        {
            Id = id,
            StreetNumber = 100,
            StreetName = "Integration Test Lane",
            PhoneNumber = null,
            EmailAddress = null,
            Residents = new List<Resident>()
        };

        await repo.UpsertAsync(home).ConfigureAwait(false);
        var loaded = await repo.GetByIdAsync(id).ConfigureAwait(false);

        Assert.NotNull(loaded);
        Assert.Equal(id, loaded.Id);
        Assert.Equal(100, loaded.StreetNumber);
        Assert.Equal("Integration Test Lane", loaded.StreetName);
    }

    [SkippableFact]
    public async Task Upsert_merges_without_dropping_extra_stored_properties()
    {
        Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

        var repo = _fixture.CreateHomeRepository();
        var id = Guid.NewGuid();
        var home = new Home
        {
            Id = id,
            StreetNumber = 200,
            StreetName = "Merge St",
            PhoneNumber = null,
            EmailAddress = null,
            Residents = new List<Resident>()
        };

        await repo.UpsertAsync(home).ConfigureAwait(false);

        // Inject a property the app does not model (like legacy EF fields); second upsert should preserve it.
        var container = _fixture.Client.GetContainer(_fixture.DatabaseId, "Homes");
        var pk = Microsoft.Azure.Cosmos.PartitionKey.None;
        var rawId = $"Home|{id:D}";
        var existing = await container.ReadItemAsync<Newtonsoft.Json.Linq.JObject>(rawId, pk).ConfigureAwait(false);
        existing.Resource["UserUniqueId"] = "legacy-user-ref";
        await container.ReplaceItemAsync(existing.Resource, rawId, pk).ConfigureAwait(false);

        home.ETag = null; // Clear stale ETag; the raw ReplaceItemAsync changed it in Cosmos.
        home.StreetNumber = 201;
        await repo.UpsertAsync(home).ConfigureAwait(false);

        var after = await container.ReadItemAsync<Newtonsoft.Json.Linq.JObject>(rawId, pk).ConfigureAwait(false);
        Assert.Equal("legacy-user-ref", after.Resource.Value<string>("UserUniqueId"));
        Assert.Equal(201, after.Resource.Value<int>("StreetNumber"));
    }
}
