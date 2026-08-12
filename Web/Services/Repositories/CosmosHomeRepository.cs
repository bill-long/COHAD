using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json.Linq;
using Web.Models;
using Web.Services.Cosmos;
using CosmosContainer = Microsoft.Azure.Cosmos.Container;
using CosmosPartitionKey = Microsoft.Azure.Cosmos.PartitionKey;
using CosmosQueryDefinition = Microsoft.Azure.Cosmos.QueryDefinition;

namespace Web.Services.Repositories
{
    public interface IHomeRepository
    {
        Task<List<Home>> GetAllAsync();
        Task<Home> GetByIdAsync(Guid id);
        Task<List<Home>> GetByIdsAsync(List<Guid> ids);
        Task<List<Home>> GetByEmailAsync(string email);
        Task<Home> UpsertAsync(Home home);
    }

    public class CosmosHomeRepository : IHomeRepository
    {
        private readonly CosmosContainer _homesContainer;

        public CosmosHomeRepository(CosmosContainer homesContainer)
        {
            _homesContainer = homesContainer;
        }

        public async Task<List<Home>> GetAllAsync()
        {
            var iterator = _homesContainer.GetItemQueryIterator<JObject>(new CosmosQueryDefinition("SELECT * FROM c"));
            var results = new List<Home>();
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                results.AddRange(response.Select(CosmosLegacyDocumentMapper.ToHome));
            }

            return results;
        }

        public async Task<Home> GetByIdAsync(Guid id)
        {
            var doc = await GetRawHomeDocumentAsync(id);
            return doc == null ? null : CosmosLegacyDocumentMapper.ToHome(doc);
        }

        public async Task<List<Home>> GetByIdsAsync(List<Guid> ids)
        {
            var candidates = ids.SelectMany(id =>
                    new[] { CosmosLegacyDocumentMapper.ToHomeDocumentId(id), id.ToString("D") }
                )
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (candidates.Count == 0)
            {
                return new List<Home>();
            }

            var idLiterals = candidates.Select(x => $"\"{x}\"");
            var query = new CosmosQueryDefinition($"SELECT * FROM c WHERE c.id IN ({string.Join(", ", idLiterals)})");
            var iterator = _homesContainer.GetItemQueryIterator<JObject>(query);
            var results = new List<Home>();
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                results.AddRange(response.Select(CosmosLegacyDocumentMapper.ToHome));
            }

            return results;
        }

        public async Task<List<Home>> GetByEmailAsync(string email)
        {
            // EmailAddress is stored as a serialized JSON string in legacy documents,
            // so we use CONTAINS as a server-side pre-filter, then exact-match client-side.
            var query = new CosmosQueryDefinition(
                "SELECT * FROM c WHERE CONTAINS(c.EmailAddress, @email, true)"
            ).WithParameter("@email", email);
            var iterator = _homesContainer.GetItemQueryIterator<JObject>(query);
            var candidates = new List<Home>();
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                candidates.AddRange(response.Select(CosmosLegacyDocumentMapper.ToHome));
            }

            return candidates
                .Where(h => string.Equals(h.EmailAddress?.Address?.Trim(), email, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        public async Task<Home> UpsertAsync(Home home)
        {
            var existing = await GetRawHomeDocumentAsync(home.Id);
            if (existing == null)
            {
                // A caller-supplied ETag asserts "this write continues from a document I read"; if
                // that document is gone, conflict rather than silently recreate it (UpsertItemAsync
                // ignores IfMatchEtag when the write materializes as a Create). Same shape as
                // CosmosUserRepository.UpsertAsync, and matches MockHomeRepository's version map.
                if (!string.IsNullOrEmpty(home.ETag))
                {
                    throw ConcurrencyConflictException.For(
                        "Home",
                        home.Id,
                        new InvalidOperationException("The document no longer exists.")
                    );
                }

                var created = CosmosLegacyDocumentMapper.ToHomeDocument(home);
                var createResponse = await _homesContainer.UpsertItemAsync(created, CosmosPartitionKey.None);
                home.ETag = createResponse.Headers.ETag;
                return home;
            }

            // The write must land on the document that was actually read: legacy documents may use
            // the unprefixed id shape, and MergeHomeIntoDocument normalizes doc["id"], which would
            // aim the write at a prefixed twin instead of the stored document. Keep the stored id.
            var storedId = existing.Value<string>("id");
            CosmosLegacyDocumentMapper.MergeHomeIntoDocument(existing, home);
            existing["id"] = storedId;
            StripCosmosSystemProperties(existing);

            if (!string.IsNullOrEmpty(home.ETag))
            {
                try
                {
                    // Replace rather than upsert: replace honors IfMatchEtag unconditionally, and a
                    // document deleted between the read above and this write fails with NotFound
                    // instead of being recreated. Both surface as the retryable conflict.
                    var requestOptions = new ItemRequestOptions { IfMatchEtag = home.ETag };
                    var response = await _homesContainer.ReplaceItemAsync(
                        existing,
                        storedId,
                        CosmosPartitionKey.None,
                        requestOptions
                    );
                    home.ETag = response.Headers.ETag;
                }
                catch (CosmosException ex)
                    when (ex.StatusCode == HttpStatusCode.PreconditionFailed || ex.StatusCode == HttpStatusCode.NotFound)
                {
                    throw ConcurrencyConflictException.For("Home", home.Id, ex);
                }
            }
            else
            {
                // Blind write: there is no precondition to lose, so a CosmosException here can only
                // be infrastructure failure and must surface raw rather than masquerade as a
                // retryable conflict.
                var response = await _homesContainer.UpsertItemAsync(existing, CosmosPartitionKey.None);
                home.ETag = response.Headers.ETag;
            }

            return home;
        }

        private async Task<JObject> GetRawHomeDocumentAsync(Guid id)
        {
            var prefixedId = CosmosLegacyDocumentMapper.ToHomeDocumentId(id);
            var candidates = new[] { prefixedId, id.ToString("D") };
            var idLiterals = candidates.Select(x => $"\"{x}\"");
            var query = new CosmosQueryDefinition($"SELECT * FROM c WHERE c.id IN ({string.Join(", ", idLiterals)})");
            var iterator = _homesContainer.GetItemQueryIterator<JObject>(query);
            var matches = new List<JObject>();
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                matches.AddRange(response);
            }

            // Twin documents (both id shapes for the same home) can exist from the era when merges
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
