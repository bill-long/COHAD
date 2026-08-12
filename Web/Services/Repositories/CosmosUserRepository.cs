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
                try
                {
                    // Create, not upsert: this branch only runs when the read found nothing, so a
                    // document appearing in between is a concurrent create, and an upsert would
                    // silently overwrite it (the same lost write the ETag branch exists to stop).
                    // Cosmos answers 409 on a duplicate id, which the catch below resolves against
                    // the winner. EF Core Cosmos containers created with no HasPartitionKey() use
                    // the default path (__partitionKey in newer tooling; portal may label it
                    // "NoPartitionKey"). Legacy docs often omit that property; writes must use
                    // PartitionKey.None so the header matches what Cosmos extracts from the payload.
                    var createResponse = await _usersContainer.CreateItemAsync(created, CosmosPartitionKey.None);
                    user.ETag = createResponse.Headers.ETag;
                    return user;
                }
                catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
                {
                    // Losing a create race is not a lost write: the winner stored the document this
                    // call was going to store (both are built from the same claims), so succeed
                    // idempotently against it rather than reporting a conflict. A first login
                    // raced by a second tab must not answer 409 - the SPA only logs that, leaving
                    // the new account signed in with no roles until a manual reload.
                    var winner = await GetRawUserDocumentAsync(user.UniqueId);
                    if (winner == null)
                    {
                        throw ConcurrencyConflictException.For("User", user.UniqueId, ex);
                    }

                    user.ETag = winner.Value<string>("_etag");
                    return user;
                }
            }

            // The write must land on the document that was actually read: MergeUserIntoDocument
            // normalizes doc["id"] to the prefixed shape, so for a document still stored under the
            // unprefixed id that would write a second document rather than update this one.
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
                    when (ex.StatusCode == HttpStatusCode.PreconditionFailed || CosmosNotFound.IsItemNotFound(ex))
                {
                    // Item-not-found only (not e.g. a provisioning-level 404, which must surface
                    // raw instead of advising a retry that can never succeed).
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
            if (string.IsNullOrEmpty(uniqueId))
            {
                // A blank id is a malformed request, not a statement about the store - there is no
                // document it could refer to. Returning quietly would let UserPurgeRunner audit a
                // deletion it never asked for against no particular document; failing loudly gets
                // it counted as an error instead, naming the record in the log.
                throw new ArgumentException("A user id is required to delete a user.", nameof(uniqueId));
            }

            // Delete the document the read path resolves, by its stored id, so reads, writes, and
            // deletes all key on the same document (the consistent-idempotency-keys rule). Deleting
            // both id shapes instead would be a two-write fan-out whose partial failure throws
            // after the account is already gone - and the purge audits only after a successful
            // delete, so that gap would permanently lose the record of a real deletion.
            var existing = await GetRawUserDocumentAsync(uniqueId);
            if (existing == null)
            {
                // No such document: an idempotent delete has nothing to do and has succeeded. This
                // is the same answer main gave (its DeleteItemAsync 404 was swallowed here too), so
                // a repeat purge of an already-deleted account still reports success rather than
                // an error.
                return;
            }

            try
            {
                await _usersContainer.DeleteItemAsync<JObject>(
                    existing.Value<string>("id"),
                    CosmosPartitionKey.None
                );
            }
            catch (CosmosException ex) when (CosmosNotFound.IsItemNotFound(ex))
            {
                // Already deleted between the read and here; the caller's intent is satisfied.
                // A non-item 404 must surface - swallowing it would let the purge audit a deletion
                // that never happened.
            }
        }

        private async Task<JObject> GetRawUserDocumentAsync(string uniqueId)
        {
            if (string.IsNullOrEmpty(uniqueId))
            {
                // No document can have a blank id. ReadItemAsync would throw ArgumentNullException
                // rather than return a 404, so guard here to keep this behaviorally identical to
                // MockUserRepository (the repository convention) and to keep a legacy document with
                // no UniqueId from aborting a purge sweep instead of being skipped.
                return null;
            }

            // Id lookups are point reads, per the repository convention. The prefixed shape is the
            // only one this app writes (creates derive it, updates keep the stored id), so it is
            // read first and the unprefixed shape is a fallback for any pre-EF-migration document
            // - which costs a second read only when the first genuinely misses, e.g. a first login.
            var prefixedId = CosmosLegacyDocumentMapper.ToUserDocumentId(uniqueId);
            var doc = await ReadDocumentIdAsync(prefixedId);
            if (doc != null || string.Equals(prefixedId, uniqueId, StringComparison.Ordinal))
            {
                return doc;
            }

            return await ReadDocumentIdAsync(uniqueId);
        }

        private async Task<JObject> ReadDocumentIdAsync(string id)
        {
            try
            {
                var response = await _usersContainer.ReadItemAsync<JObject>(id, CosmosPartitionKey.None);
                return response.Resource;
            }
            catch (CosmosException ex) when (CosmosNotFound.IsItemNotFound(ex))
            {
                return null;
            }
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
