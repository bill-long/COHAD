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
    public interface IYouthServiceListingRepository
    {
        Task<List<YouthServiceListing>> GetAllAsync();
        Task<YouthServiceListing> GetByIdAsync(Guid id);
        Task<YouthServiceListing> UpsertAsync(YouthServiceListing listing);
        Task DeleteAsync(Guid id);
    }

    public class CosmosYouthServiceListingRepository : IYouthServiceListingRepository
    {
        private readonly CosmosContainer _youthServicesContainer;

        public CosmosYouthServiceListingRepository(CosmosContainer youthServicesContainer)
        {
            _youthServicesContainer = youthServicesContainer;
        }

        public async Task<List<YouthServiceListing>> GetAllAsync()
        {
            var iterator = _youthServicesContainer.GetItemQueryIterator<JObject>(new CosmosQueryDefinition("SELECT * FROM c"));
            var results = new List<YouthServiceListing>();
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                results.AddRange(response.Select(ToYouthService));
            }

            return results;
        }

        public async Task<YouthServiceListing> GetByIdAsync(Guid id)
        {
            var query = new CosmosQueryDefinition("SELECT * FROM c WHERE c.id = @id")
                .WithParameter("@id", ToDocumentId(id));
            var iterator = _youthServicesContainer.GetItemQueryIterator<JObject>(query);
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                var doc = response.FirstOrDefault();
                if (doc != null)
                {
                    return ToYouthService(doc);
                }
            }

            return null;
        }

        public async Task<YouthServiceListing> UpsertAsync(YouthServiceListing listing)
        {
            if (listing.Id == Guid.Empty)
            {
                listing.Id = Guid.NewGuid();
            }

            await _youthServicesContainer.UpsertItemAsync(ToYouthServiceDocument(listing), CosmosPartitionKey.None);
            return listing;
        }

        public async Task DeleteAsync(Guid id)
        {
            try
            {
                await _youthServicesContainer.DeleteItemAsync<JObject>(ToDocumentId(id), CosmosPartitionKey.None);
            }
            catch (Microsoft.Azure.Cosmos.CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // idempotent
            }
        }

        private static string ToDocumentId(Guid id) => $"YouthService|{id:D}";

        private static YouthServiceListing ToYouthService(JObject doc)
        {
            var rawId = doc.Value<string>("id") ?? string.Empty;
            var parts = rawId.Split('|', StringSplitOptions.RemoveEmptyEntries);
            var parsed = Guid.TryParse(parts.LastOrDefault(), out var id) ? id : Guid.Empty;

            return new YouthServiceListing
            {
                Id = parsed,
                Name = doc.Value<string>("Name"),
                Services = doc["Services"]?.ToObject<List<string>>() ?? new List<string>(),
                BornYear = doc.Value<int?>("BornYear"),
                Phone = doc.Value<string>("Phone"),
                ContactMethod = Enum.TryParse<PreferredContactMethod>(doc.Value<string>("ContactMethod"), out var contactMethod)
                    ? contactMethod
                    : PreferredContactMethod.Text,
                Email = doc.Value<string>("Email"),
                Address = doc.Value<string>("Address"),
                ParentNote = doc.Value<string>("ParentNote"),
                CreatedByUniqueId = doc.Value<string>("CreatedByUniqueId"),
                ModifiedByUniqueId = doc.Value<string>("ModifiedByUniqueId"),
                CreatedUtc = doc["CreatedUtc"]?.ToObject<DateTime>() ?? DateTime.MinValue,
                ModifiedUtc = doc["ModifiedUtc"]?.ToObject<DateTime>() ?? DateTime.MinValue
            };
        }

        private static JObject ToYouthServiceDocument(YouthServiceListing listing)
        {
            return new JObject
            {
                ["id"] = ToDocumentId(listing.Id),
                ["Discriminator"] = "YouthServiceListing",
                ["Name"] = listing.Name,
                ["Services"] = JToken.FromObject(listing.Services ?? new List<string>()),
                ["BornYear"] = listing.BornYear != null ? JToken.FromObject(listing.BornYear) : JValue.CreateNull(),
                ["Phone"] = listing.Phone ?? string.Empty,
                ["ContactMethod"] = listing.ContactMethod.ToString(),
                ["Email"] = listing.Email ?? string.Empty,
                ["Address"] = listing.Address ?? string.Empty,
                ["ParentNote"] = listing.ParentNote ?? string.Empty,
                ["CreatedByUniqueId"] = listing.CreatedByUniqueId,
                ["ModifiedByUniqueId"] = listing.ModifiedByUniqueId,
                ["CreatedUtc"] = JToken.FromObject(listing.CreatedUtc),
                ["ModifiedUtc"] = JToken.FromObject(listing.ModifiedUtc)
            };
        }

    }
}
