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
using CosmosException = Microsoft.Azure.Cosmos.CosmosException;
using CosmosItemRequestOptions = Microsoft.Azure.Cosmos.ItemRequestOptions;
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
        Task<List<User>> GetPurgeCandidatesAsync(DateTime cutoffUtc);

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
            if (existing == null)
            {
                // A caller-supplied ETag asserts "this write continues from a document I read". If
                // that document is gone, the snapshot is stale in the strongest way - the user was
                // deleted after the read (e.g. by the purge) - and writing would silently resurrect
                // the account: UpsertItemAsync ignores IfMatchEtag when the write materializes as a
                // Create. Surface the same retryable conflict a stale update gets.
                if (!string.IsNullOrEmpty(user.ETag))
                {
                    throw ConcurrencyConflictException.For(
                        "User",
                        user.UniqueId,
                        new InvalidOperationException("The document no longer exists.")
                    );
                }

                var created = CosmosLegacyDocumentMapper.ToUserDocument(user);
                // EF Core Cosmos containers created with no HasPartitionKey() use the default path (__partitionKey in
                // newer tooling; portal may label it "NoPartitionKey"). Legacy docs often omit that property; writes must
                // use PartitionKey.None so the header matches what Cosmos extracts from the payload.
                var createResponse = await _usersContainer.UpsertItemAsync(created, CosmosPartitionKey.None);
                user.ETag = createResponse.Headers.ETag;
                return user;
            }

            // The write must land on the document that was actually read: legacy documents may use
            // the unprefixed id shape, and MergeUserIntoDocument normalizes doc["id"], which would
            // aim the write at a prefixed twin instead of the stored document. Keep the stored id.
            var storedId = existing.Value<string>("id");
            CosmosLegacyDocumentMapper.MergeUserIntoDocument(existing, user);
            existing["id"] = storedId;
            StripCosmosSystemProperties(existing);

            if (!string.IsNullOrEmpty(user.ETag))
            {
                try
                {
                    // Replace rather than upsert: replace honors IfMatchEtag unconditionally, and a
                    // document deleted between the read above and this write fails with NotFound
                    // instead of being recreated. Both surface as the retryable conflict.
                    // (PartitionKey.None for the same reason as the create path above.)
                    var requestOptions = new CosmosItemRequestOptions { IfMatchEtag = user.ETag };
                    var response = await _usersContainer.ReplaceItemAsync(
                        existing,
                        storedId,
                        CosmosPartitionKey.None,
                        requestOptions
                    );
                    user.ETag = response.Headers.ETag;
                }
                catch (CosmosException ex)
                    when (ex.StatusCode == HttpStatusCode.PreconditionFailed || ex.StatusCode == HttpStatusCode.NotFound)
                {
                    throw ConcurrencyConflictException.For("User", user.UniqueId, ex);
                }
            }
            else
            {
                // Blind write: there is no precondition to lose, so a CosmosException here can only
                // be infrastructure failure and must surface raw rather than masquerade as a
                // retryable conflict.
                var response = await _usersContainer.UpsertItemAsync(existing, CosmosPartitionKey.None);
                user.ETag = response.Headers.ETag;
            }

            return user;
        }

        public async Task<List<User>> GetPurgeCandidatesAsync(DateTime cutoffUtc)
        {
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
            while (iterator.HasMoreResults)
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
                    }
                }
            }

            return results;
        }

        public async Task DeleteAsync(string uniqueId)
        {
            // Delete every id shape the read path recognizes (the consistent-idempotency-keys rule:
            // reads, writes, and deletes must key on the same ids). Legacy documents may be stored
            // under the unprefixed id - and upserts deliberately preserve the stored id - so
            // deleting only the prefixed shape would leave such a document alive while the purge's
            // audit entry says it was removed.
            var candidateIds = new[] { CosmosLegacyDocumentMapper.ToUserDocumentId(uniqueId), uniqueId }.Distinct();
            await Task.WhenAll(candidateIds.Select(DeleteDocumentIdAsync));
        }

        private async Task DeleteDocumentIdAsync(string id)
        {
            try
            {
                await _usersContainer.DeleteItemAsync<JObject>(id, CosmosPartitionKey.None);
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // Idempotent delete
            }
        }

        private async Task<JObject> GetRawUserDocumentAsync(string uniqueId)
        {
            var prefixedId = CosmosLegacyDocumentMapper.ToUserDocumentId(uniqueId);
            var candidateIds = new[] { prefixedId, uniqueId };
            var idLiterals = candidateIds.Select(id => $"\"{id}\"");
            var query = new CosmosQueryDefinition($"SELECT * FROM c WHERE c.id IN ({string.Join(", ", idLiterals)})");
            var iterator = _usersContainer.GetItemQueryIterator<JObject>(query);
            var matches = new List<JObject>();
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                matches.AddRange(response);
            }

            // Twin documents (both id shapes for the same user) can exist from the era when merges
            // were written to the normalized id while the original document remained. Prefer the
            // prefixed shape deterministically, so the ETag a caller read and the document this
            // class writes always refer to the same twin.
            return matches.FirstOrDefault(d => d.Value<string>("id") == prefixedId) ?? matches.FirstOrDefault();
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
