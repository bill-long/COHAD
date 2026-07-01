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
        /// Returns the held message for the given Internet Message-ID in the specified committee, or null.
        /// </summary>
        Task<HeldMessage?> GetByInternetMessageIdAsync(string committeeId, string internetMessageId);

        Task UpdateAsync(HeldMessage message);

        /// <summary>
        /// Returns held messages for the given committee, ordered by HeldUtc descending.
        /// When <paramref name="status"/> is supplied, the filter is applied in the query so the
        /// limit counts only matching rows (otherwise older matching rows can be crowded out by
        /// more-recent rows of other statuses).
        /// </summary>
        Task<List<HeldMessage>> GetByCommitteeIdAsync(string committeeId, int limit = 50, HeldMessageStatus? status = null);

        /// <summary>
        /// Returns all held messages with status <see cref="HeldMessageStatus.Held"/> that are
        /// older than the given cutoff. Used for expiry cleanup.
        /// </summary>
        Task<List<HeldMessage>> GetExpiredAsync(DateTime cutoffUtc, int limit = 100);

        /// <summary>
        /// Returns held messages with status <see cref="HeldMessageStatus.Held"/> that have not yet been
        /// notified (<see cref="HeldMessage.NotifiedUtc"/> is null) and whose <see cref="HeldMessage.HeldUtc"/>
        /// is at or before <paramref name="heldBeforeUtc"/>. Used to surface messages to moderators only
        /// after the antispam quarantine window has elapsed. Ordered oldest-held first.
        /// </summary>
        Task<List<HeldMessage>> GetAwaitingNotificationAsync(DateTime heldBeforeUtc, int limit = 100);
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

        public async Task<HeldMessage?> GetByInternetMessageIdAsync(string committeeId, string internetMessageId)
        {
            if (string.IsNullOrEmpty(internetMessageId))
                return null;

            var query = new CosmosQueryDefinition(
                "SELECT TOP 1 * FROM c WHERE c.CommitteeId = @committeeId AND c.InternetMessageId = @internetMessageId"
            )
                .WithParameter("@committeeId", committeeId)
                .WithParameter("@internetMessageId", internetMessageId);

            var iterator = _container.GetItemQueryIterator<JObject>(query);
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                var first = response.FirstOrDefault();
                if (first != null)
                {
                    var msg = CosmosLegacyDocumentMapper.ToHeldMessage(first);
                    msg.ETag = first.Value<string>("_etag");
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

        public async Task<List<HeldMessage>> GetByCommitteeIdAsync(string committeeId, int limit = 50, HeldMessageStatus? status = null)
        {
            var clampedLimit = Math.Clamp(limit, 1, 200);
            var statusFilter = status.HasValue ? "AND c.Status = @status " : string.Empty;
            var query = new CosmosQueryDefinition(
                $"SELECT TOP {clampedLimit} * FROM c WHERE c.CommitteeId = @committeeId {statusFilter}ORDER BY c.HeldUtc DESC"
            ).WithParameter("@committeeId", committeeId);
            if (status.HasValue)
                query = query.WithParameter("@status", status.Value.ToString());

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

        public async Task<List<HeldMessage>> GetAwaitingNotificationAsync(DateTime heldBeforeUtc, int limit = 100)
        {
            var clampedLimit = Math.Clamp(limit, 1, 250);
            var query = new CosmosQueryDefinition(
                $"SELECT TOP {clampedLimit} * FROM c "
                    + "WHERE c.Status = @held AND c.HeldUtc <= @heldBeforeUtc "
                    + "AND (NOT IS_DEFINED(c.NotifiedUtc) OR IS_NULL(c.NotifiedUtc)) "
                    + "ORDER BY c.HeldUtc ASC"
            )
                .WithParameter("@held", nameof(HeldMessageStatus.Held))
                .WithParameter("@heldBeforeUtc", heldBeforeUtc);

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
