using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Web.Models;
using Web.Services.Cosmos;
using CosmosContainer = Microsoft.Azure.Cosmos.Container;
using CosmosPartitionKey = Microsoft.Azure.Cosmos.PartitionKey;
using CosmosQueryDefinition = Microsoft.Azure.Cosmos.QueryDefinition;

namespace Web.Services.Repositories
{
    public interface IUserRepository
    {
        Task<List<User>> GetAllAsync();
        Task<User> GetByUniqueIdAsync(string uniqueId);
        Task<User> UpsertAsync(User user);
    }

    public class CosmosUserRepository : IUserRepository
    {
        private readonly CosmosContainer _usersContainer;

        public CosmosUserRepository(CosmosContainer usersContainer)
        {
            _usersContainer = usersContainer;
        }

        public async Task<List<User>> GetAllAsync()
        {
            var iterator = _usersContainer.GetItemQueryIterator<JObject>(new CosmosQueryDefinition("SELECT * FROM c"));
            var results = new List<User>();
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                results.AddRange(response.Select(CosmosLegacyDocumentMapper.ToUser));
            }

            return results;
        }

        public async Task<User> GetByUniqueIdAsync(string uniqueId)
        {
            var doc = await GetRawUserDocumentAsync(uniqueId);
            return doc == null ? null : CosmosLegacyDocumentMapper.ToUser(doc);
        }

        public async Task<User> UpsertAsync(User user)
        {
            var existing = await GetRawUserDocumentAsync(user.UniqueId);
            JObject doc;
            if (existing != null)
            {
                CosmosLegacyDocumentMapper.MergeUserIntoDocument(existing, user);
                StripCosmosSystemProperties(existing);
                doc = existing;
            }
            else
            {
                doc = CosmosLegacyDocumentMapper.ToUserDocument(user);
            }

            // EF Core Cosmos containers created with no HasPartitionKey() use the default path (__partitionKey in
            // newer tooling; portal may label it "NoPartitionKey"). Legacy docs often omit that property; writes must
            // use PartitionKey.None so the header matches what Cosmos extracts from the payload.
            await _usersContainer.UpsertItemAsync(doc, CosmosPartitionKey.None);
            return user;
        }

        private async Task<JObject> GetRawUserDocumentAsync(string uniqueId)
        {
            var candidateIds = new[] { CosmosLegacyDocumentMapper.ToUserDocumentId(uniqueId), uniqueId };
            var idLiterals = candidateIds.Select(id => $"\"{id}\"");
            var query = new CosmosQueryDefinition($"SELECT * FROM c WHERE c.id IN ({string.Join(", ", idLiterals)})");
            var iterator = _usersContainer.GetItemQueryIterator<JObject>(query);
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                var doc = response.FirstOrDefault();
                if (doc != null)
                {
                    return doc;
                }
            }

            return null;
        }

        private static void StripCosmosSystemProperties(JObject doc)
        {
            doc.Remove("_rid");
            doc.Remove("_self");
            doc.Remove("_etag");
            doc.Remove("_attachments");
            doc.Remove("_ts");
        }
    }
}
