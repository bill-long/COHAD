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
        await repo.AddAsync(
                new NewAuditLogEntry
                {
                    Id = Guid.NewGuid(),
                    Time = DateTime.UtcNow,
                    UserId = "user-1",
                    UserDisplayName = "Tester",
                    SubjectId = "subject",
                    SubjectName = "Subject",
                    Action = $"Integration test audit {marker}",
                }
            )
            .ConfigureAwait(false);

        var all = await repo.GetAllAsync().ConfigureAwait(false);
        Assert.Contains(all, e => e.Action != null && e.Action.Contains(marker, StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task GetPageAsync_supports_search_and_descending_time_order()
    {
        Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

        var repo = _fixture.CreateAuditLogRepository();
        var marker = $"PagedSearch-{Guid.NewGuid():N}";
        var baseTime = DateTime.UtcNow;

        await repo.AddAsync(
                new NewAuditLogEntry
                {
                    Id = Guid.NewGuid(),
                    Time = baseTime.AddMinutes(-3),
                    UserId = "user-1",
                    UserDisplayName = "Tester",
                    SubjectId = "subject-1",
                    SubjectName = "Subject",
                    Action = $"{marker} first",
                }
            )
            .ConfigureAwait(false);

        await repo.AddAsync(
                new NewAuditLogEntry
                {
                    Id = Guid.NewGuid(),
                    Time = baseTime.AddMinutes(-2),
                    UserId = "user-2",
                    UserDisplayName = "Tester",
                    SubjectId = "subject-2",
                    SubjectName = "Subject",
                    Action = $"{marker} second",
                }
            )
            .ConfigureAwait(false);

        await repo.AddAsync(
                new NewAuditLogEntry
                {
                    Id = Guid.NewGuid(),
                    Time = baseTime.AddMinutes(-1),
                    UserId = "user-3",
                    UserDisplayName = "Tester",
                    SubjectId = "subject-3",
                    SubjectName = "Subject",
                    Action = $"{marker} third",
                }
            )
            .ConfigureAwait(false);

        var firstPage = await repo.GetPageAsync(2, null, marker).ConfigureAwait(false);

        // Cosmos MaxItemCount is a hint, not a hard limit; the server page may contain
        // more items than requested, so assert at-least rather than exact count.
        Assert.InRange(firstPage.Items.Count, 2, 3);
        Assert.True(firstPage.Items[0].Time >= firstPage.Items[1].Time);
        Assert.All(firstPage.Items, item => Assert.Contains(marker, item.Action, StringComparison.Ordinal));

        if (!string.IsNullOrWhiteSpace(firstPage.ContinuationToken))
        {
            var secondPage = await repo.GetPageAsync(2, firstPage.ContinuationToken, marker).ConfigureAwait(false);
            Assert.All(secondPage.Items, item => Assert.Contains(marker, item.Action, StringComparison.Ordinal));
        }
    }
}
