using Web.IntegrationTests.Support;
using Web.Models;

namespace Web.IntegrationTests.Repositories;

[Collection("Cosmos integration")]
public sealed class UserRepositoryIntegrationTests
{
    private readonly CosmosIntegrationFixture _fixture;

    public UserRepositoryIntegrationTests(CosmosIntegrationFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Upsert_and_GetByUniqueId_round_trips_without_partition_key_errors()
    {
        Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

        var repo = _fixture.CreateUserRepository();
        var suffix = Guid.NewGuid().ToString("N");
        var uniqueId = $"google.com{suffix}";
        var user = new User
        {
            UniqueId = uniqueId,
            NameIdentifier = suffix,
            GivenName = "Test",
            Surname = "User",
            IdentityProvider = "google.com",
            Emails = "test@example.com",
            StreetAddress = "1 Test St",
            Roles = new List<User.Role> { User.Role.Resident },
            OwnedHomeIds = new List<Guid>(),
            LastLoggedIn = DateTime.UtcNow,
        };

        await repo.UpsertAsync(user).ConfigureAwait(false);
        var loaded = await repo.GetByUniqueIdAsync(uniqueId).ConfigureAwait(false);

        Assert.NotNull(loaded);
        Assert.Equal(uniqueId, loaded.UniqueId);
        Assert.Equal("Test", loaded.GivenName);
    }
}
