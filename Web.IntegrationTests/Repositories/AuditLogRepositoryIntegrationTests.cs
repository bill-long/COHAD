using Web.IntegrationTests.Support;
using Web.Models;

namespace Web.IntegrationTests.Repositories;

[Collection("Cosmos integration")]
public sealed class AuditLogRepositoryIntegrationTests
{
    private readonly CosmosIntegrationFixture _fixture;

    public AuditLogRepositoryIntegrationTests(CosmosIntegrationFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task AddAsync_does_not_throw_partition_key_errors()
    {
        Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

        var repo = _fixture.CreateAuditLogRepository();
        var marker = Guid.NewGuid().ToString();
        await repo.AddAsync(new NewAuditLogEntry
        {
            Id = Guid.NewGuid(),
            Time = DateTime.UtcNow,
            UserId = "user-1",
            UserDisplayName = "Tester",
            SubjectId = "subject",
            SubjectName = "Subject",
            Action = $"Integration test audit {marker}"
        }).ConfigureAwait(false);

        var all = await repo.GetAllAsync().ConfigureAwait(false);
        Assert.Contains(all, e => e.Action != null && e.Action.Contains(marker, StringComparison.Ordinal));
    }
}
