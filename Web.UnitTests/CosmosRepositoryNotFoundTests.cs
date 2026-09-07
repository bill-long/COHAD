using System;
using System.Collections.Generic;
using System.Linq;
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

public class CosmosRepositoryNotFoundTests
{
    private static readonly Guid Id = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly Dictionary<string, Func<Container, Task>> Operations = new()
    {
        ["EmailJob.Read"] = async c => Assert.Null(await new CosmosEmailJobRepository(c).GetByIdAsync(Id)),
        ["EmailJob.Delete"] = c => new CosmosEmailJobRepository(c).DeleteAsync(Id),
        ["HeldMessage.Read"] = async c => Assert.Null(await new CosmosHeldMessageRepository(c).GetByIdAsync(Id)),
        ["Resident.Read"] = async c => Assert.Null(await new CosmosResidentRepository(c).GetByIdAsync(Id)),
        ["Resident.Delete"] = c => new CosmosResidentRepository(c).DeleteAsync(Id),
        ["Committee.Read"] = async c => Assert.Null(await new CosmosCommitteeRepository(c).GetByIdAsync("social")),
        ["Notification.Read"] = async c => Assert.Null(await new CosmosNotificationRepository(c).GetByIdAsync(Id)),
        ["BlogComment.Read"] = async c => Assert.Null(await new CosmosBlogCommentRepository(c).GetByIdAsync(Id)),
        ["BlogComment.Delete"] = c => new CosmosBlogCommentRepository(c).DeleteAsync(Id),
        ["BlogPost.Get"] = async c => Assert.Null(await new CosmosBlogPostRepository(c).GetByIdAsync(Id)),
        ["BlogPost.Read"] = async c => Assert.Null(await new CosmosBlogPostRepository(c).ReadAsync(Id)),
        ["BlogPost.Delete"] = c => new CosmosBlogPostRepository(c).DeleteAsync(Id),
        ["Event.Read"] = async c => Assert.Null(await new CosmosCommunityEventRepository(c).ReadAsync(Id)),
        ["Event.Delete"] = c => new CosmosCommunityEventRepository(c).DeleteAsync(Id),
        ["Document.Delete"] = c => new CosmosDocumentRepository(c).DeleteAsync(Id),
        ["DocumentFolder.Delete"] = c => new CosmosDocumentFolderRepository(c).DeleteAsync(Id),
        ["Vendor.Delete"] = c => new CosmosVendorRepository(c).DeleteAsync(Id),
        ["VendorReview.Delete"] = c => new CosmosVendorReviewRepository(c).DeleteAsync(Id, Id),
        ["VendorReview.Cascade"] = c => new CosmosVendorReviewRepository(c).DeleteByVendorCascadeAsync(Id, Id),
        ["VendorFlag.Delete"] = c => new CosmosVendorFlagRepository(c).DeleteAsync(Id, Id),
        ["VendorFlag.Cascade"] = c => new CosmosVendorFlagRepository(c).DeleteByVendorCascadeAsync(Id, Id),
        ["YouthService.Delete"] = c => new CosmosYouthServiceListingRepository(c).DeleteAsync(Id),
        ["BackgroundJob.Read"] = async c =>
            Assert.Null(await new CosmosBackgroundJobStateRepository(c).GetAsync("job")),
        ["DigestState.Read"] = async c =>
            Assert.Null(await new CosmosNotificationDigestStateRepository(c).GetAsync("resident@example.com")),
        ["UnsubscribeLink.Read"] = async c =>
            Assert.Null(await new CosmosUnsubscribeLinkRepository(c).GetByIdAsync("link")),
        ["Suppression.Read"] = async c =>
            Assert.Null(await new CosmosEmailSuppressionRepository(c).GetByIdAsync("suppression")),
        ["DeliveryEvent.DeleteFallback"] = c => new CosmosEmailDeliveryEventRepository(c).DeleteByJobIdAsync(Id),
    };

    public static IEnumerable<object[]> Cases =>
        Operations.Keys.SelectMany(operation =>
            new[]
            {
                new object[] { operation, HttpStatusCode.NotFound, 0 },
                new object[] { operation, HttpStatusCode.NotFound, 1003 },
                new object[] { operation, HttpStatusCode.ServiceUnavailable, 0 },
            }
        );

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Only_missing_items_are_treated_as_absent(string operation, HttpStatusCode status, int subStatus)
    {
        var failure = new CosmosException("Storage failure", status, subStatus, "activity", 0);
        var container = new Mock<Container>();
        ConfigurePointFailures<JObject>(container, failure);
        ConfigurePointFailures<BlogPost>(container, failure);
        // Vendor deletes first query for ownership; delivery cleanup queries ids before deleting a batch.
        var feed = new Mock<FeedResponse<JObject>>();
        feed.Setup(f => f.GetEnumerator())
            .Returns(() =>
                new List<JObject>
                {
                    new() { ["id"] = Id.ToString(), ["VendorId"] = Id.ToString() },
                }.GetEnumerator()
            );
        var iterator = new Mock<FeedIterator<JObject>>();
        iterator.SetupSequence(i => i.HasMoreResults).Returns(true).Returns(false);
        iterator.Setup(i => i.ReadNextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(feed.Object);
        container
            .Setup(c =>
                c.GetItemQueryIterator<JObject>(
                    It.IsAny<QueryDefinition>(),
                    It.IsAny<string>(),
                    It.IsAny<QueryRequestOptions>()
                )
            )
            .Returns(iterator.Object);
        var batch = new Mock<TransactionalBatch>();
        batch
            .Setup(b => b.ExecuteAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<TransactionalBatchResponse>());
        container.Setup(c => c.CreateTransactionalBatch(It.IsAny<PartitionKey>())).Returns(batch.Object);

        if (status == HttpStatusCode.NotFound && subStatus == 0)
            await Operations[operation](container.Object);
        else
            Assert.Same(
                failure,
                await Assert.ThrowsAsync<CosmosException>(() => Operations[operation](container.Object))
            );

        Assert.Single(container.Invocations, i => i.Method.Name is "ReadItemAsync" or "DeleteItemAsync");
    }

    private static void ConfigurePointFailures<T>(Mock<Container> container, CosmosException failure)
    {
        container
            .Setup(c =>
                c.ReadItemAsync<T>(
                    It.IsAny<string>(),
                    It.IsAny<PartitionKey>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ThrowsAsync(failure);
        container
            .Setup(c =>
                c.DeleteItemAsync<T>(
                    It.IsAny<string>(),
                    It.IsAny<PartitionKey>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ThrowsAsync(failure);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1003)]
    public async Task Delivery_patch_recreates_only_a_missing_item(int subStatus)
    {
        var failure = new CosmosException("Missing", HttpStatusCode.NotFound, subStatus, "activity", 0);
        var container = new Mock<Container>();
        container
            .SetupSequence(c =>
                c.CreateItemAsync(
                    It.IsAny<JObject>(),
                    It.IsAny<PartitionKey?>(),
                    It.IsAny<ItemRequestOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ThrowsAsync(new CosmosException("Exists", HttpStatusCode.Conflict, 0, "activity", 0))
            .ReturnsAsync(Mock.Of<ItemResponse<JObject>>());
        container
            .Setup(c =>
                c.PatchItemAsync<JObject>(
                    It.IsAny<string>(),
                    It.IsAny<PartitionKey>(),
                    It.IsAny<IReadOnlyList<PatchOperation>>(),
                    It.IsAny<PatchItemRequestOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ThrowsAsync(failure);
        var repo = new CosmosEmailDeliveryEventRepository(container.Object);
        var delivery = new EmailDeliveryEvent { Id = "delivery", JobId = Id };

        if (subStatus == 0)
            await repo.AddAsync(delivery);
        else
            Assert.Same(failure, await Assert.ThrowsAsync<CosmosException>(() => repo.AddAsync(delivery)));

        Assert.Equal(subStatus == 0 ? 2 : 1, container.Invocations.Count(i => i.Method.Name == "CreateItemAsync"));
    }
}
