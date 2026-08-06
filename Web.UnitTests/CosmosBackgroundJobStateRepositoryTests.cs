using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Moq;
using Newtonsoft.Json.Linq;
using Web.Models;
using Web.Services.Repositories;
using Xunit;

namespace Web.UnitTests;

public sealed class CosmosBackgroundJobStateRepositoryTests
{
    private static Mock<Container> ContainerThrowing(HttpStatusCode status, int subStatusCode)
    {
        var container = new Mock<Container>(MockBehavior.Loose);
        container
            .Setup(c =>
                c.ReadItemAsync<JObject>(
                    It.IsAny<string>(),
                    It.IsAny<PartitionKey>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ThrowsAsync(new CosmosException("nope", status, subStatusCode, "activity", 0));
        return container;
    }

    [Fact]
    public async Task Get_returns_null_when_the_document_is_genuinely_missing()
    {
        var repo = new CosmosBackgroundJobStateRepository(ContainerThrowing(HttpStatusCode.NotFound, 0).Object);

        Assert.Null(await repo.GetAsync("paypal-sync"));
    }

    [Fact]
    public async Task Get_surfaces_a_missing_container_instead_of_reporting_never_run()
    {
        // A 404 with a non-zero sub-status means the container/database was never provisioned. Swallowing
        // it would make every tick look like a first run, so an interval-paced job would fire on every
        // app start - for the PayPal sync, that means hitting the live API on every deploy.
        var repo = new CosmosBackgroundJobStateRepository(ContainerThrowing(HttpStatusCode.NotFound, 1003).Object);

        var ex = await Assert.ThrowsAsync<CosmosException>(() => repo.GetAsync("paypal-sync"));
        Assert.Equal(1003, ex.SubStatusCode);
    }

    [Fact]
    public async Task Get_maps_both_timestamps_and_the_point_read_etag()
    {
        // If this mapping silently returned DateTime.MinValue, both jobs would look "never run" and fire
        // on every tick - hitting the live PayPal API on every deploy - with the rest of the suite green.
        var success = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var attempt = new DateTime(2026, 6, 2, 12, 0, 0, DateTimeKind.Utc);
        var doc = new JObject
        {
            ["id"] = "BackgroundJobState|paypal-sync",
            ["JobName"] = "paypal-sync",
            ["LastSuccessUtc"] = JToken.FromObject(success),
            ["LastAttemptUtc"] = JToken.FromObject(attempt),
            ["_etag"] = "\"stale\"",
        };

        var headers = new Mock<Headers>();
        headers.Setup(h => h.ETag).Returns("\"etag-9\"");
        var response = new Mock<ItemResponse<JObject>>();
        response.Setup(r => r.Resource).Returns(doc);
        response.Setup(r => r.Headers).Returns(headers.Object);

        var container = new Mock<Container>();
        container
            .Setup(c =>
                c.ReadItemAsync<JObject>(
                    "BackgroundJobState|paypal-sync",
                    It.IsAny<PartitionKey>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(response.Object);

        var repo = new CosmosBackgroundJobStateRepository(container.Object);

        var state = await repo.GetAsync("paypal-sync");

        Assert.NotNull(state);
        Assert.Equal("paypal-sync", state!.JobName);
        Assert.Equal(success, state.LastSuccessUtc);
        Assert.Equal(attempt, state.LastAttemptUtc);
        // The point-read header wins over the document body's _etag.
        Assert.Equal("\"etag-9\"", state.ETag);
    }

    [Fact]
    public async Task Get_returns_null_for_a_blank_job_name_without_touching_cosmos()
    {
        var container = new Mock<Container>(MockBehavior.Strict);
        var repo = new CosmosBackgroundJobStateRepository(container.Object);

        Assert.Null(await repo.GetAsync("   "));
        container.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Upsert_writes_back_the_response_etag()
    {
        // Parity with MockBackgroundJobStateRepository, which stamps an ETag on every upsert. The repo
        // convention is that ETag is populated on every read *and* write path.
        var headers = new Mock<Headers>();
        headers.Setup(h => h.ETag).Returns("\"etag-1\"");
        var response = new Mock<ItemResponse<JObject>>();
        response.Setup(r => r.Headers).Returns(headers.Object);

        var container = new Mock<Container>();
        container
            .Setup(c =>
                c.UpsertItemAsync(
                    It.IsAny<JObject>(),
                    It.IsAny<PartitionKey?>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(response.Object);

        var repo = new CosmosBackgroundJobStateRepository(container.Object);
        var state = new BackgroundJobState { JobName = "paypal-sync" };

        await repo.UpsertAsync(state);

        Assert.Equal("\"etag-1\"", state.ETag);
    }

    [Fact]
    public async Task Upsert_writes_the_normalized_job_name_and_both_timestamps()
    {
        var headers = new Mock<Headers>();
        headers.Setup(h => h.ETag).Returns("\"etag-1\"");
        var response = new Mock<ItemResponse<JObject>>();
        response.Setup(r => r.Headers).Returns(headers.Object);

        JObject? written = null;
        var container = new Mock<Container>();
        container
            .Setup(c =>
                c.UpsertItemAsync(
                    It.IsAny<JObject>(),
                    It.IsAny<PartitionKey?>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback<JObject, PartitionKey?, ItemRequestOptions, CancellationToken>((doc, _, _, _) => written = doc)
            .ReturnsAsync(response.Object);

        var repo = new CosmosBackgroundJobStateRepository(container.Object);
        var success = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var attempt = new DateTime(2026, 6, 2, 12, 0, 0, DateTimeKind.Utc);

        await repo.UpsertAsync(
            new BackgroundJobState
            {
                JobName = "  PayPal-Sync  ",
                LastSuccessUtc = success,
                LastAttemptUtc = attempt,
            }
        );

        Assert.NotNull(written);
        Assert.Equal("BackgroundJobState|paypal-sync", written!.Value<string>("id"));
        Assert.Equal("paypal-sync", written.Value<string>("JobName"));
        Assert.Equal(success, written["LastSuccessUtc"]!.ToObject<DateTime>());
        Assert.Equal(attempt, written["LastAttemptUtc"]!.ToObject<DateTime>());
    }

    [Fact]
    public async Task Upsert_ignores_a_blank_job_name_without_touching_cosmos()
    {
        // Mirrors the Mock impl: a blank name maps to a shared document id and would clobber another job.
        var container = new Mock<Container>(MockBehavior.Strict);
        var repo = new CosmosBackgroundJobStateRepository(container.Object);

        await repo.UpsertAsync(new BackgroundJobState { JobName = "   " });

        container.VerifyNoOtherCalls();
    }
}
