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
    public interface ICommunityEventRepository
    {
        Task<List<CommunityEvent>> GetAllAsync();

        /// <summary>
        /// Events with <see cref="CommunityEvent.StartUtc"/> &gt;= <paramref name="minStartUtcInclusive"/> (for upcoming lists without scanning the whole container).
        /// </summary>
        Task<List<CommunityEvent>> GetWithStartUtcOnOrAfterAsync(DateTime minStartUtcInclusive);

        Task<CommunityEvent> GetByIdAsync(Guid id);

        /// <summary>
        /// Resolves <paramref name="segment"/> as the current <see cref="CommunityEvent.PublicSlug"/> or a previous slug alias.
        /// </summary>
        Task<CommunityEvent> GetByRouteSegmentAsync(string segment);

        /// <summary>
        /// Point read by id (includes <see cref="CommunityEventReadResult.ETag"/> for <see cref="ReplaceAsync"/>).
        /// </summary>
        Task<CommunityEventReadResult> ReadAsync(Guid id);

        Task<CommunityEvent> UpsertAsync(CommunityEvent communityEvent);

        /// <summary>
        /// Replaces the document only if the ETag matches (Cosmos <c>If-Match</c>).
        /// </summary>
        /// <exception cref="CosmosException">Thrown when the ETag does not match (412) or the item is gone (404).</exception>
        Task<CommunityEvent> ReplaceAsync(CommunityEvent communityEvent, string ifMatchEtag);

        Task DeleteAsync(Guid id);
    }

    public class CosmosCommunityEventRepository : ICommunityEventRepository
    {
        private readonly CosmosContainer _eventsContainer;

        public CosmosCommunityEventRepository(CosmosContainer eventsContainer)
        {
            _eventsContainer = eventsContainer;
        }

        public async Task<List<CommunityEvent>> GetAllAsync()
        {
            var iterator = _eventsContainer.GetItemQueryIterator<JObject>(new CosmosQueryDefinition("SELECT * FROM c"));
            var results = new List<CommunityEvent>();
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                results.AddRange(response.Select(CosmosLegacyDocumentMapper.ToCommunityEvent));
            }

            return results;
        }

        public async Task<List<CommunityEvent>> GetWithStartUtcOnOrAfterAsync(DateTime minStartUtcInclusive)
        {
            var query = new CosmosQueryDefinition("SELECT * FROM c WHERE c.StartUtc >= @min")
                .WithParameter("@min", minStartUtcInclusive);
            var iterator = _eventsContainer.GetItemQueryIterator<JObject>(query);
            var results = new List<CommunityEvent>();
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                results.AddRange(response.Select(CosmosLegacyDocumentMapper.ToCommunityEvent));
            }

            return results;
        }

        public async Task<CommunityEvent> GetByIdAsync(Guid id)
        {
            var query = new CosmosQueryDefinition("SELECT * FROM c WHERE c.id = @id")
                .WithParameter("@id", CosmosLegacyDocumentMapper.ToCommunityEventId(id));
            var iterator = _eventsContainer.GetItemQueryIterator<JObject>(query);
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                var doc = response.FirstOrDefault();
                if (doc != null)
                {
                    return CosmosLegacyDocumentMapper.ToCommunityEvent(doc);
                }
            }

            return null;
        }

        public async Task<CommunityEvent> GetByRouteSegmentAsync(string segment)
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                return null;
            }

            var normalizedSlug = segment.Trim().ToLowerInvariant();

            var slugQuery = new CosmosQueryDefinition("SELECT * FROM c WHERE c.PublicSlug = @slug")
                .WithParameter("@slug", normalizedSlug);
            var slugIterator = _eventsContainer.GetItemQueryIterator<JObject>(slugQuery);
            while (slugIterator.HasMoreResults)
            {
                var response = await slugIterator.ReadNextAsync();
                var doc = response.FirstOrDefault();
                if (doc != null)
                {
                    return CosmosLegacyDocumentMapper.ToCommunityEvent(doc);
                }
            }

            var aliasQuery = new CosmosQueryDefinition("SELECT * FROM c WHERE ARRAY_CONTAINS(c.PreviousSlugs, @slug)")
                .WithParameter("@slug", normalizedSlug);
            var aliasIterator = _eventsContainer.GetItemQueryIterator<JObject>(aliasQuery);
            while (aliasIterator.HasMoreResults)
            {
                var response = await aliasIterator.ReadNextAsync();
                var doc = response.FirstOrDefault();
                if (doc != null)
                {
                    return CosmosLegacyDocumentMapper.ToCommunityEvent(doc);
                }
            }

            return null;
        }

        public async Task<CommunityEventReadResult> ReadAsync(Guid id)
        {
            var documentId = CosmosLegacyDocumentMapper.ToCommunityEventId(id);
            try
            {
                var response = await _eventsContainer.ReadItemAsync<JObject>(documentId, CosmosPartitionKey.None);
                return new CommunityEventReadResult
                {
                    Event = CosmosLegacyDocumentMapper.ToCommunityEvent(response.Resource),
                    ETag = response.ETag
                };
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        public async Task<CommunityEvent> UpsertAsync(CommunityEvent communityEvent)
        {
            if (communityEvent.Id == Guid.Empty)
            {
                communityEvent.Id = Guid.NewGuid();
            }

            var doc = CosmosLegacyDocumentMapper.ToCommunityEventDocument(communityEvent);
            await _eventsContainer.UpsertItemAsync(doc, CosmosPartitionKey.None);
            return communityEvent;
        }

        public async Task<CommunityEvent> ReplaceAsync(CommunityEvent communityEvent, string ifMatchEtag)
        {
            var doc = CosmosLegacyDocumentMapper.ToCommunityEventDocument(communityEvent);
            var documentId = doc.Value<string>("id");
            var options = new ItemRequestOptions { IfMatchEtag = ifMatchEtag };
            var response = await _eventsContainer.ReplaceItemAsync(
                doc,
                documentId,
                CosmosPartitionKey.None,
                options);
            return CosmosLegacyDocumentMapper.ToCommunityEvent(response.Resource);
        }

        public async Task DeleteAsync(Guid id)
        {
            var documentId = CosmosLegacyDocumentMapper.ToCommunityEventId(id);
            try
            {
                await _eventsContainer.DeleteItemAsync<JObject>(documentId, CosmosPartitionKey.None);
            }
            catch (Microsoft.Azure.Cosmos.CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Idempotent delete
            }
        }
    }
}
