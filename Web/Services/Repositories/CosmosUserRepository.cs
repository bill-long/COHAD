using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Web.Models;
using Web.Services;
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

        /// <summary>
        /// Users whose no-home or no-role clock is on or before <paramref name="cutoffUtc"/>.
        /// </summary>
        Task<List<User>> GetPurgeCandidatesAsync(DateTime cutoffUtc, int maxCount);

        Task DeleteAsync(string uniqueId);
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
            UserAssociationState.Apply(user);

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

        public async Task<List<User>> GetPurgeCandidatesAsync(DateTime cutoffUtc, int maxCount)
        {
            if (maxCount < 1)
            {
                return new List<User>();
            }

            // OwnedHomeIds and Roles are commonly stored as JSON strings (legacy).
            var query = new CosmosQueryDefinition(
                @"
SELECT * FROM c
WHERE c.Discriminator = 'User'
  AND (
    (
      IS_DEFINED(c.UnassociatedSinceUtc)
      AND c.UnassociatedSinceUtc <= @cutoff
      AND (
        NOT IS_DEFINED(c.OwnedHomeIds)
        OR c.OwnedHomeIds = '[]'
        OR (IS_ARRAY(c.OwnedHomeIds) AND ARRAY_LENGTH(c.OwnedHomeIds) = 0)
      )
    )
    OR
    (
      IS_DEFINED(c.NoRolesSinceUtc)
      AND c.NoRolesSinceUtc <= @cutoff
      AND (
        NOT IS_DEFINED(c.Roles)
        OR c.Roles = '[]'
        OR (IS_ARRAY(c.Roles) AND ARRAY_LENGTH(c.Roles) = 0)
      )
    )
  )"
            ).WithParameter("@cutoff", cutoffUtc);

            var results = new List<User>();
            var iterator = _usersContainer.GetItemQueryIterator<JObject>(query);
            while (iterator.HasMoreResults && results.Count < maxCount)
            {
                var response = await iterator.ReadNextAsync();
                foreach (var doc in response)
                {
                    var user = CosmosLegacyDocumentMapper.ToUser(doc);
                    var noHomesEligible =
                        (user.OwnedHomeIds == null || user.OwnedHomeIds.Count == 0)
                        && user.UnassociatedSinceUtc != null
                        && user.UnassociatedSinceUtc <= cutoffUtc;
                    var noRolesEligible =
                        (user.Roles == null || user.Roles.Count == 0)
                        && user.NoRolesSinceUtc != null
                        && user.NoRolesSinceUtc <= cutoffUtc;
                    if (noHomesEligible || noRolesEligible)
                    {
                        results.Add(user);
                        if (results.Count >= maxCount)
                        {
                            break;
                        }
                    }
                }
            }

            return results;
        }

        public async Task DeleteAsync(string uniqueId)
        {
            var id = CosmosLegacyDocumentMapper.ToUserDocumentId(uniqueId);
            try
            {
                await _usersContainer.DeleteItemAsync<JObject>(id, CosmosPartitionKey.None);
            }
            catch (Microsoft.Azure.Cosmos.CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // Idempotent delete
            }
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
