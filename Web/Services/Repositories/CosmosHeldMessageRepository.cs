#nullable enable
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
    public interface IHeldMessageRepository
    {
        Task AddAsync(HeldMessage message);

        Task<HeldMessage?> GetByIdAsync(Guid id);

        /// <summary>
        /// Returns the held message for the given Graph message ID in the specified committee, or null.
        /// </summary>
        Task<HeldMessage?> GetByGraphMessageIdAsync(string committeeId, string graphMessageId);

        Task UpdateAsync(HeldMessage message);

        /// <summary>
        /// Returns held messages for the given committee, ordered by HeldUtc descending.
        /// </summary>
        Task<List<HeldMessage>> GetByCommitteeIdAsync(string committeeId, int limit = 50);

        /// <summary>
        /// Returns all held messages with status <see cref="HeldMessageStatus.Held"/> that are
        /// older than the given cutoff. Used for expiry cleanup.
        /// </summary>
        Task<List<HeldMessage>> GetExpiredAsync(DateTime cutoffUtc, int limit = 100);
    }

    public class CosmosHeldMessageRepository : IHeldMessageRepository
    {
        private readonly CosmosContainer _container;

        public CosmosHeldMessageRepository(CosmosContainer container)
        {
            _container = container;
        }

        public async Task AddAsync(HeldMessage message)
        {
            var doc = CosmosLegacyDocumentMapper.ToHeldMessageDocument(message);
            await _container.CreateItemAsync(doc, CosmosPartitionKey.None);
        }

        public async Task<HeldMessage?> GetByIdAsync(Guid id)
        {
            var documentId = CosmosLegacyDocumentMapper.ToHeldMessageDocumentId(id);
            try
            {
                var response = await _container.ReadItemAsync<JObject>(documentId, CosmosPartitionKey.None);
                var msg = CosmosLegacyDocumentMapper.ToHeldMessage(response.Resource);
                msg.ETag = response.Headers.ETag;
                return msg;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        public async Task<HeldMessage?> GetByGraphMessageIdAsync(string committeeId, string graphMessageId)
        {
            if (string.IsNullOrEmpty(graphMessageId))
                return null;

            var query = new CosmosQueryDefinition(
                "SELECT TOP 1 * FROM c WHERE c.CommitteeId = @committeeId AND c.GraphMessageId = @graphMessageId"
            )
                .WithParameter("@committeeId", committeeId)
                .WithParameter("@graphMessageId", graphMessageId);

            var iterator = _container.GetItemQueryIterator<JObject>(query);
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                var first = response.FirstOrDefault();
                if (first != null)
                {
                    var msg = CosmosLegacyDocumentMapper.ToHeldMessage(first);
                    msg.ETag = response.Headers.ETag;
                    return msg;
                }
            }

            return null;
        }

        public async Task UpdateAsync(HeldMessage message)
        {
            var doc = CosmosLegacyDocumentMapper.ToHeldMessageDocument(message);
            var documentId = doc.Value<string>("id");
            var requestOptions = new ItemRequestOptions();
            if (!string.IsNullOrEmpty(message.ETag))
                requestOptions.IfMatchEtag = message.ETag;

            try
            {
                var response = await _container.ReplaceItemAsync(
                    doc,
                    documentId,
                    CosmosPartitionKey.None,
                    requestOptions
                );
                message.ETag = response.Headers.ETag;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                throw new InvalidOperationException("HeldMessage was modified by another process.", ex);
            }
        }

        public async Task<List<HeldMessage>> GetByCommitteeIdAsync(string committeeId, int limit = 50)
        {
            var clampedLimit = Math.Clamp(limit, 1, 200);
            var query = new CosmosQueryDefinition(
                $"SELECT TOP {clampedLimit} * FROM c WHERE c.CommitteeId = @committeeId ORDER BY c.HeldUtc DESC"
            ).WithParameter("@committeeId", committeeId);

            var iterator = _container.GetItemQueryIterator<JObject>(query);
            var results = new List<HeldMessage>();
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                results.AddRange(response.Select(CosmosLegacyDocumentMapper.ToHeldMessage));
            }

            return results;
        }

        public async Task<List<HeldMessage>> GetExpiredAsync(DateTime cutoffUtc, int limit = 100)
        {
            var clampedLimit = Math.Clamp(limit, 1, 250);
            var query = new CosmosQueryDefinition(
                $"SELECT TOP {clampedLimit} * FROM c "
                    + "WHERE c.Status = @held AND c.HeldUtc < @cutoffUtc ORDER BY c.HeldUtc ASC"
            )
                .WithParameter("@held", nameof(HeldMessageStatus.Held))
                .WithParameter("@cutoffUtc", cutoffUtc);

            var iterator = _container.GetItemQueryIterator<JObject>(query);
            var results = new List<HeldMessage>();
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                results.AddRange(response.Select(CosmosLegacyDocumentMapper.ToHeldMessage));
            }

            return results;
        }
    }
}
