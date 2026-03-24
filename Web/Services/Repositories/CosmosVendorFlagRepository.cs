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
    public interface IVendorFlagRepository
    {
        Task<List<VendorFlag>> GetByVendorIdAsync(Guid vendorId);
        Task<VendorFlag> GetByIdAsync(Guid vendorId, Guid flagId);
        Task<VendorFlag> GetPendingByAuthorAsync(Guid vendorId, string authorUniqueId);
        Task<List<VendorFlag>> GetAllPendingAsync();
        Task<VendorFlag> UpsertAsync(VendorFlag flag);
        Task DeleteAsync(Guid vendorId, Guid flagId);
        /// <summary>Deletes by id without an extra read. Use only when ids came from <see cref="GetByVendorIdAsync"/> for <paramref name="vendorId"/> (e.g. vendor cascade delete).</summary>
        Task DeleteByVendorCascadeAsync(Guid vendorId, Guid flagId);
    }

    public class CosmosVendorFlagRepository : IVendorFlagRepository
    {
        private readonly CosmosContainer _vendorFlagsContainer;

        public CosmosVendorFlagRepository(CosmosContainer vendorFlagsContainer)
        {
            _vendorFlagsContainer = vendorFlagsContainer;
        }

        public async Task<List<VendorFlag>> GetByVendorIdAsync(Guid vendorId)
        {
            var query = new CosmosQueryDefinition("SELECT * FROM c WHERE c.VendorId = @vendorId")
                .WithParameter("@vendorId", vendorId.ToString("D"));
            var iterator = _vendorFlagsContainer.GetItemQueryIterator<JObject>(query);
            var results = new List<VendorFlag>();
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                results.AddRange(response.Select(ToVendorFlag));
            }

            return results;
        }

        public async Task<VendorFlag> GetByIdAsync(Guid vendorId, Guid flagId)
        {
            var query = new CosmosQueryDefinition("SELECT * FROM c WHERE c.id = @id AND c.VendorId = @vendorId")
                .WithParameter("@id", ToDocumentId(flagId))
                .WithParameter("@vendorId", vendorId.ToString("D"));
            var iterator = _vendorFlagsContainer.GetItemQueryIterator<JObject>(query);
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                var doc = response.FirstOrDefault();
                if (doc != null)
                {
                    return ToVendorFlag(doc);
                }
            }

            return null;
        }

        public async Task<VendorFlag> GetPendingByAuthorAsync(Guid vendorId, string authorUniqueId)
        {
            var query = new CosmosQueryDefinition(
                    "SELECT * FROM c WHERE c.VendorId = @vendorId AND c.AuthorUniqueId = @authorUniqueId AND c.Status = 'Pending'")
                .WithParameter("@vendorId", vendorId.ToString("D"))
                .WithParameter("@authorUniqueId", authorUniqueId);
            var iterator = _vendorFlagsContainer.GetItemQueryIterator<JObject>(query);
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                var doc = response.FirstOrDefault();
                if (doc != null)
                {
                    return ToVendorFlag(doc);
                }
            }

            return null;
        }

        public async Task<List<VendorFlag>> GetAllPendingAsync()
        {
            var query = new CosmosQueryDefinition("SELECT * FROM c WHERE c.Status = 'Pending'");
            var iterator = _vendorFlagsContainer.GetItemQueryIterator<JObject>(query);
            var results = new List<VendorFlag>();
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                results.AddRange(response.Select(ToVendorFlag));
            }

            return results;
        }

        public async Task<VendorFlag> UpsertAsync(VendorFlag flag)
        {
            if (flag.Id == Guid.Empty)
            {
                flag.Id = Guid.NewGuid();
            }

            await _vendorFlagsContainer.UpsertItemAsync(ToVendorFlagDocument(flag), CosmosPartitionKey.None);
            return flag;
        }

        public async Task DeleteAsync(Guid vendorId, Guid flagId)
        {
            var existing = await GetByIdAsync(vendorId, flagId);
            if (existing == null)
            {
                return;
            }

            try
            {
                await _vendorFlagsContainer.DeleteItemAsync<JObject>(ToDocumentId(flagId), CosmosPartitionKey.None);
            }
            catch (Microsoft.Azure.Cosmos.CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // idempotent
            }
        }

        public async Task DeleteByVendorCascadeAsync(Guid vendorId, Guid flagId)
        {
            _ = vendorId;
            try
            {
                await _vendorFlagsContainer.DeleteItemAsync<JObject>(ToDocumentId(flagId), CosmosPartitionKey.None);
            }
            catch (Microsoft.Azure.Cosmos.CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // idempotent
            }
        }

        private static string ToDocumentId(Guid id) => $"VendorFlag|{id:D}";

        private static VendorFlag ToVendorFlag(JObject doc)
        {
            var rawId = doc.Value<string>("id") ?? string.Empty;
            var parts = rawId.Split('|', StringSplitOptions.RemoveEmptyEntries);
            var parsedId = Guid.TryParse(parts.LastOrDefault(), out var id) ? id : Guid.Empty;
            var vendorRaw = doc.Value<string>("VendorId");
            var parsedVendor = Guid.TryParse(vendorRaw, out var vendorId) ? vendorId : Guid.Empty;

            return new VendorFlag
            {
                Id = parsedId,
                VendorId = parsedVendor,
                AuthorUniqueId = doc.Value<string>("AuthorUniqueId"),
                AuthorDisplayName = doc.Value<string>("AuthorDisplayName"),
                FlagNote = doc.Value<string>("FlagNote"),
                Status = doc.Value<string>("Status"),
                CreatedUtc = doc["CreatedUtc"]?.ToObject<DateTime>() ?? DateTime.MinValue,
                ResolvedUtc = doc["ResolvedUtc"]?.ToObject<DateTime>() ?? DateTime.MinValue
            };
        }

        private static JObject ToVendorFlagDocument(VendorFlag flag)
        {
            return new JObject
            {
                ["id"] = ToDocumentId(flag.Id),
                ["Discriminator"] = "VendorFlag",
                ["VendorId"] = flag.VendorId.ToString("D"),
                ["AuthorUniqueId"] = flag.AuthorUniqueId,
                ["AuthorDisplayName"] = flag.AuthorDisplayName,
                ["FlagNote"] = flag.FlagNote ?? string.Empty,
                ["Status"] = flag.Status,
                ["CreatedUtc"] = JToken.FromObject(flag.CreatedUtc),
                ["ResolvedUtc"] = JToken.FromObject(flag.ResolvedUtc)
            };
        }
    }
}
