using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Web.Models;
using CosmosContainer = Microsoft.Azure.Cosmos.Container;
using CosmosPartitionKey = Microsoft.Azure.Cosmos.PartitionKey;
using CosmosQueryDefinition = Microsoft.Azure.Cosmos.QueryDefinition;

namespace Web.Services.Repositories
{
    public interface IVendorReviewRepository
    {
        Task<List<VendorReview>> GetByVendorIdAsync(Guid vendorId);
        Task<VendorReview> GetByIdAsync(Guid vendorId, Guid reviewId);
        Task<VendorReview> UpsertAsync(VendorReview review);
        Task DeleteAsync(Guid vendorId, Guid reviewId);

        /// <summary>Deletes by id without an extra read. Use only when ids came from <see cref="GetByVendorIdAsync"/> for <paramref name="vendorId"/> (e.g. vendor cascade delete).</summary>
        Task DeleteByVendorCascadeAsync(Guid vendorId, Guid reviewId);
        Task<VendorReview> GetByVendorAndAuthorAsync(Guid vendorId, string authorUniqueId);
        Task<IReadOnlyDictionary<Guid, int>> GetReviewCountsByVendorAsync();
        Task<IReadOnlyDictionary<Guid, DateTime>> GetLatestReviewModifiedUtcByVendorAsync();
    }

    public class CosmosVendorReviewRepository : IVendorReviewRepository
    {
        private readonly CosmosContainer _vendorReviewsContainer;

        public CosmosVendorReviewRepository(CosmosContainer vendorReviewsContainer)
        {
            _vendorReviewsContainer = vendorReviewsContainer;
        }

        public async Task<List<VendorReview>> GetByVendorIdAsync(Guid vendorId)
        {
            var query = new CosmosQueryDefinition("SELECT * FROM c WHERE c.VendorId = @vendorId").WithParameter(
                "@vendorId",
                vendorId.ToString("D")
            );
            var iterator = _vendorReviewsContainer.GetItemQueryIterator<JObject>(query);
            var results = new List<VendorReview>();
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                results.AddRange(response.Select(ToVendorReview));
            }

            return results;
        }

        public async Task<VendorReview> GetByIdAsync(Guid vendorId, Guid reviewId)
        {
            var query = new CosmosQueryDefinition("SELECT * FROM c WHERE c.id = @id AND c.VendorId = @vendorId")
                .WithParameter("@id", ToDocumentId(reviewId))
                .WithParameter("@vendorId", vendorId.ToString("D"));
            var iterator = _vendorReviewsContainer.GetItemQueryIterator<JObject>(query);
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                var doc = response.FirstOrDefault();
                if (doc != null)
                {
                    return ToVendorReview(doc);
                }
            }

            return null;
        }

        public async Task<VendorReview> GetByVendorAndAuthorAsync(Guid vendorId, string authorUniqueId)
        {
            var query = new CosmosQueryDefinition(
                "SELECT * FROM c WHERE c.VendorId = @vendorId AND c.AuthorUniqueId = @authorUniqueId"
            )
                .WithParameter("@vendorId", vendorId.ToString("D"))
                .WithParameter("@authorUniqueId", authorUniqueId);
            var iterator = _vendorReviewsContainer.GetItemQueryIterator<JObject>(query);
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                var doc = response.FirstOrDefault();
                if (doc != null)
                {
                    return ToVendorReview(doc);
                }
            }

            return null;
        }

        public async Task<VendorReview> UpsertAsync(VendorReview review)
        {
            if (review.Id == Guid.Empty)
            {
                review.Id = Guid.NewGuid();
            }

            await _vendorReviewsContainer.UpsertItemAsync(ToVendorReviewDocument(review), CosmosPartitionKey.None);
            return review;
        }

        public async Task DeleteAsync(Guid vendorId, Guid reviewId)
        {
            var existing = await GetByIdAsync(vendorId, reviewId);
            if (existing == null)
            {
                return;
            }

            try
            {
                await _vendorReviewsContainer.DeleteItemAsync<JObject>(ToDocumentId(reviewId), CosmosPartitionKey.None);
            }
            catch (Microsoft.Azure.Cosmos.CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // idempotent
            }
        }

        public async Task DeleteByVendorCascadeAsync(Guid vendorId, Guid reviewId)
        {
            _ = vendorId;
            try
            {
                await _vendorReviewsContainer.DeleteItemAsync<JObject>(ToDocumentId(reviewId), CosmosPartitionKey.None);
            }
            catch (Microsoft.Azure.Cosmos.CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // idempotent
            }
        }

        public async Task<IReadOnlyDictionary<Guid, int>> GetReviewCountsByVendorAsync()
        {
            var query = new CosmosQueryDefinition("SELECT c.VendorId, COUNT(1) AS cnt FROM c GROUP BY c.VendorId");
            var iterator = _vendorReviewsContainer.GetItemQueryIterator<JObject>(query);
            var counts = new Dictionary<Guid, int>();
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                foreach (var row in response)
                {
                    var vendorRaw = row.Value<string>("VendorId");
                    if (!Guid.TryParse(vendorRaw, out var vendorId))
                    {
                        continue;
                    }

                    var cntToken = row["cnt"];
                    var count =
                        cntToken?.Type == JTokenType.Integer ? cntToken.Value<int>() : cntToken?.Value<int?>() ?? 0;
                    counts[vendorId] = count;
                }
            }

            return counts;
        }

        public async Task<IReadOnlyDictionary<Guid, DateTime>> GetLatestReviewModifiedUtcByVendorAsync()
        {
            var query = new CosmosQueryDefinition(
                "SELECT c.VendorId, MAX(c.ModifiedUtc) AS latestModifiedUtc FROM c GROUP BY c.VendorId"
            );
            var iterator = _vendorReviewsContainer.GetItemQueryIterator<JObject>(query);
            var latestByVendor = new Dictionary<Guid, DateTime>();
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                foreach (var row in response)
                {
                    var vendorRaw = row.Value<string>("VendorId");
                    if (!Guid.TryParse(vendorRaw, out var vendorId))
                    {
                        continue;
                    }

                    var latest = row["latestModifiedUtc"]?.ToObject<DateTime?>();
                    if (latest.HasValue)
                    {
                        latestByVendor[vendorId] = latest.Value;
                    }
                }
            }

            return latestByVendor;
        }

        private static string ToDocumentId(Guid id) => $"VendorReview|{id:D}";

        private static VendorReview ToVendorReview(JObject doc)
        {
            var rawId = doc.Value<string>("id") ?? string.Empty;
            var parts = rawId.Split('|', StringSplitOptions.RemoveEmptyEntries);
            var parsedId = Guid.TryParse(parts.LastOrDefault(), out var id) ? id : Guid.Empty;
            var vendorRaw = doc.Value<string>("VendorId");
            var parsedVendor = Guid.TryParse(vendorRaw, out var vendorId) ? vendorId : Guid.Empty;

            return new VendorReview
            {
                Id = parsedId,
                VendorId = parsedVendor,
                AuthorUniqueId = doc.Value<string>("AuthorUniqueId"),
                AuthorDisplayName = doc.Value<string>("AuthorDisplayName"),
                ReviewText = doc.Value<string>("ReviewText"),
                CreatedUtc = doc["CreatedUtc"]?.ToObject<DateTime>() ?? DateTime.MinValue,
                ModifiedUtc = doc["ModifiedUtc"]?.ToObject<DateTime>() ?? DateTime.MinValue,
            };
        }

        private static JObject ToVendorReviewDocument(VendorReview review)
        {
            return new JObject
            {
                ["id"] = ToDocumentId(review.Id),
                ["Discriminator"] = "VendorReview",
                ["VendorId"] = review.VendorId.ToString("D"),
                ["AuthorUniqueId"] = review.AuthorUniqueId,
                ["AuthorDisplayName"] = review.AuthorDisplayName,
                ["ReviewText"] = review.ReviewText ?? string.Empty,
                ["CreatedUtc"] = JToken.FromObject(review.CreatedUtc),
                ["ModifiedUtc"] = JToken.FromObject(review.ModifiedUtc),
            };
        }
    }
}
