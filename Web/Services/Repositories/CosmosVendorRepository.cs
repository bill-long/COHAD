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
    public interface IVendorRepository
    {
        Task<List<Vendor>> GetAllAsync();
        Task<Vendor> GetByIdAsync(Guid id);
        Task<Vendor> UpsertAsync(Vendor vendor);
        Task DeleteAsync(Guid id);
    }

    public class CosmosVendorRepository : IVendorRepository
    {
        private readonly CosmosContainer _vendorsContainer;

        public CosmosVendorRepository(CosmosContainer vendorsContainer)
        {
            _vendorsContainer = vendorsContainer;
        }

        public async Task<List<Vendor>> GetAllAsync()
        {
            var iterator = _vendorsContainer.GetItemQueryIterator<JObject>(
                new CosmosQueryDefinition("SELECT * FROM c")
            );
            var results = new List<Vendor>();
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                results.AddRange(response.Select(ToVendor));
            }

            return results;
        }

        public async Task<Vendor> GetByIdAsync(Guid id)
        {
            var query = new CosmosQueryDefinition("SELECT * FROM c WHERE c.id = @id").WithParameter(
                "@id",
                ToDocumentId(id)
            );
            var iterator = _vendorsContainer.GetItemQueryIterator<JObject>(query);
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                var doc = response.FirstOrDefault();
                if (doc != null)
                {
                    return ToVendor(doc);
                }
            }

            return null;
        }

        public async Task<Vendor> UpsertAsync(Vendor vendor)
        {
            if (vendor.Id == Guid.Empty)
            {
                vendor.Id = Guid.NewGuid();
            }

            var doc = ToVendorDocument(vendor);
            await _vendorsContainer.UpsertItemAsync(doc, CosmosPartitionKey.None);
            return vendor;
        }

        public async Task DeleteAsync(Guid id)
        {
            try
            {
                await _vendorsContainer.DeleteItemAsync<JObject>(ToDocumentId(id), CosmosPartitionKey.None);
            }
            catch (Microsoft.Azure.Cosmos.CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // idempotent
            }
        }

        private static string ToDocumentId(Guid id) => $"Vendor|{id:D}";

        private static Vendor ToVendor(JObject doc)
        {
            var rawId = doc.Value<string>("id") ?? string.Empty;
            var parts = rawId.Split('|', StringSplitOptions.RemoveEmptyEntries);
            var parsed = Guid.TryParse(parts.LastOrDefault(), out var id) ? id : Guid.Empty;
            return new Vendor
            {
                Id = parsed,
                Name = doc.Value<string>("Name"),
                Categories = doc["Categories"]?.ToObject<List<string>>() ?? new List<string>(),
                IsNeighborAffiliated = doc["IsNeighborAffiliated"]?.ToObject<bool>() ?? false,
                Phone = doc.Value<string>("Phone"),
                Email = doc.Value<string>("Email"),
                Website = doc.Value<string>("Website"),
                Address = doc.Value<string>("Address"),
                Notes = doc.Value<string>("Notes"),
                CreatedByUniqueId = doc.Value<string>("CreatedByUniqueId"),
                ModifiedByUniqueId = doc.Value<string>("ModifiedByUniqueId"),
                CreatedUtc = doc["CreatedUtc"]?.ToObject<DateTime>() ?? DateTime.MinValue,
                ModifiedUtc = doc["ModifiedUtc"]?.ToObject<DateTime>() ?? DateTime.MinValue,
            };
        }

        private static JObject ToVendorDocument(Vendor vendor)
        {
            return new JObject
            {
                ["id"] = ToDocumentId(vendor.Id),
                ["Discriminator"] = "Vendor",
                ["Name"] = vendor.Name,
                ["Categories"] = JToken.FromObject(vendor.Categories ?? new List<string>()),
                ["IsNeighborAffiliated"] = vendor.IsNeighborAffiliated,
                ["Phone"] = vendor.Phone ?? string.Empty,
                ["Email"] = vendor.Email ?? string.Empty,
                ["Website"] = vendor.Website ?? string.Empty,
                ["Address"] = vendor.Address ?? string.Empty,
                ["Notes"] = vendor.Notes ?? string.Empty,
                ["CreatedByUniqueId"] = vendor.CreatedByUniqueId,
                ["ModifiedByUniqueId"] = vendor.ModifiedByUniqueId,
                ["CreatedUtc"] = JToken.FromObject(vendor.CreatedUtc),
                ["ModifiedUtc"] = JToken.FromObject(vendor.ModifiedUtc),
            };
        }
    }
}
