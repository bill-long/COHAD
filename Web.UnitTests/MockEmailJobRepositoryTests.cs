using System;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Web.MockData;
using Web.Models;
using Xunit;

namespace Web.UnitTests;

public sealed class MockEmailJobRepositoryTests
{
    private static EmailJob Job(Guid id) =>
        new EmailJob
        {
            Id = id,
            Status = EmailJobStatus.Queued,
            Category = "test",
            FromEmail = "from@cohad.org",
            FromDisplay = "From",
            Subject = "s",
            CreatedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CreatedByUserId = "system:test",
            CreatedByDisplayName = "Test",
            MaxRecipientAttempts = 3,
        };

    [Fact]
    public async Task AddAsync_throws_conflict_on_duplicate_id_and_keeps_original()
    {
        var repo = new MockEmailJobRepository(new MockDocumentFileStore());
        var id = Guid.NewGuid();
        var original = Job(id);
        await repo.AddAsync(original);

        // A second add with the same id must 409 (matching Cosmos CreateItemAsync), not overwrite. The
        // poller assigns deterministic ids and depends on this 409 to dedup concurrent forwarding jobs.
        var duplicate = Job(id);
        duplicate.Subject = "overwritten";
        var ex = await Assert.ThrowsAsync<CosmosException>(() => repo.AddAsync(duplicate));
        Assert.Equal(HttpStatusCode.Conflict, ex.StatusCode);

        // The original record is untouched (not re-enqueued with new content).
        var stored = await repo.GetByIdAsync(id);
        Assert.NotNull(stored);
        Assert.Equal("s", stored!.Subject);
    }

    [Fact]
    public async Task Reads_preserve_the_party_fields()
    {
        // The mock clones jobs on every read; a field missing from the clone would silently disappear
        // here but survive in Cosmos, which is exactly the divergence the repository convention forbids.
        var repo = new MockEmailJobRepository(new MockDocumentFileStore());
        var id = Guid.NewGuid();
        var job = Job(id);
        job.Category = EmailJob.CommitteeForwardCategory;
        job.ToDisplay = "Architectural Committee forwarding members";
        job.OriginalSenderEmail = "jane@example.com";
        job.OriginalSenderDisplay = "Jane Doe";
        await repo.AddAsync(job);

        var stored = await repo.GetByIdAsync(id);

        Assert.NotNull(stored);
        Assert.Equal("Architectural Committee forwarding members", stored!.ToDisplay);
        Assert.Equal("jane@example.com", stored.OriginalSenderEmail);
        Assert.Equal("Jane Doe", stored.OriginalSenderDisplay);
    }
}
