using Web.IntegrationTests.Support;
using Web.Models;

namespace Web.IntegrationTests.Repositories;

/// <summary>Mirrors HomeController.Update ordering: audit entry then home upsert.</summary>
[Collection("Cosmos integration")]
public sealed class HomeUpdateScenarioIntegrationTests
{
    private readonly CosmosIntegrationFixture _fixture;

    public HomeUpdateScenarioIntegrationTests(CosmosIntegrationFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task AuditLog_then_HomeUpsert_matches_controller_flow()
    {
        Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

        var homes = _fixture.CreateHomeRepository();
        var audit = _fixture.CreateAuditLogRepository();

        var homeId = Guid.NewGuid();
        var home = new Home
        {
            Id = homeId,
            StreetNumber = 300,
            StreetName = "Flow Test",
            PhoneNumber = null,
            EmailAddress = null,
            Residents = new List<Resident>(),
        };

        await homes.UpsertAsync(home).ConfigureAwait(false);

        var marker = Guid.NewGuid().ToString("N");
        await audit
            .AddAsync(
                new NewAuditLogEntry
                {
                    Id = Guid.NewGuid(),
                    Time = DateTime.UtcNow,
                    UserId = $"google.com{Guid.NewGuid():N}",
                    UserDisplayName = "Flow User",
                    SubjectId = homeId.ToString(),
                    SubjectName = home.StreetName,
                    Action = $"Flow audit {marker}",
                }
            )
            .ConfigureAwait(false);

        home.StreetNumber = 301;
        await homes.UpsertAsync(home).ConfigureAwait(false);

        var reloaded = await homes.GetByIdAsync(homeId).ConfigureAwait(false);
        Assert.NotNull(reloaded);
        Assert.Equal(301, reloaded.StreetNumber);

        var audits = await audit.GetAllAsync().ConfigureAwait(false);
        Assert.Contains(audits, a => a.Action != null && a.Action.Contains(marker, StringComparison.Ordinal));
    }
}
