#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Web.Models;
using Web.Services.Cosmos;
using CosmosContainer = Microsoft.Azure.Cosmos.Container;
using CosmosPartitionKey = Microsoft.Azure.Cosmos.PartitionKey;
using CosmosQueryDefinition = Microsoft.Azure.Cosmos.QueryDefinition;

namespace Web.Services.Repositories
{
    public interface IAuditLogRepository
    {
        Task AddAsync(NewAuditLogEntry entry);
        Task<List<NewAuditLogEntry>> GetAllAsync();
        Task<AuditLogPage> GetPageAsync(int limit, string? continuationToken, string? searchQuery);
    }

    public class CosmosAuditLogRepository : IAuditLogRepository
    {
        private readonly CosmosContainer _auditLogContainer;
        private readonly ILogger<CosmosAuditLogRepository> _logger;

        public CosmosAuditLogRepository(CosmosContainer auditLogContainer, ILogger<CosmosAuditLogRepository> logger)
        {
            _auditLogContainer = auditLogContainer;
            _logger = logger;
        }

        public async Task AddAsync(NewAuditLogEntry entry)
        {
            var doc = CosmosLegacyDocumentMapper.ToAuditLogDocument(entry);
            // Matches legacy EF Core Cosmos containers that use the default single-partition ("NoPartitionKey") layout.
            await _auditLogContainer.CreateItemAsync(doc, CosmosPartitionKey.None);
        }

        public async Task<List<NewAuditLogEntry>> GetAllAsync()
        {
            var iterator = _auditLogContainer.GetItemQueryIterator<JObject>(new CosmosQueryDefinition("SELECT * FROM c"));
            var results = new List<NewAuditLogEntry>();
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                results.AddRange(response.Select(CosmosLegacyDocumentMapper.ToAuditLog));
            }

            return results;
        }

        public async Task<AuditLogPage> GetPageAsync(int limit, string? continuationToken, string? searchQuery)
        {
            var pageSize = Math.Clamp(limit, 1, 200);
            var hasSearch = !string.IsNullOrWhiteSpace(searchQuery);
            var normalizedSearch = hasSearch ? searchQuery!.Trim().ToLowerInvariant() : null;

            var queryText = hasSearch
                ? @"SELECT * FROM c
                    WHERE CONTAINS(LOWER(IIF(IS_DEFINED(c.UserDisplayName) AND NOT IS_NULL(c.UserDisplayName), c.UserDisplayName, '')), @query)
                       OR CONTAINS(LOWER(IIF(IS_DEFINED(c.UserId) AND NOT IS_NULL(c.UserId), c.UserId, '')), @query)
                       OR CONTAINS(LOWER(IIF(IS_DEFINED(c.SubjectName) AND NOT IS_NULL(c.SubjectName), c.SubjectName, '')), @query)
                       OR CONTAINS(LOWER(IIF(IS_DEFINED(c.SubjectId) AND NOT IS_NULL(c.SubjectId), c.SubjectId, '')), @query)
                       OR CONTAINS(LOWER(IIF(IS_DEFINED(c.Action) AND NOT IS_NULL(c.Action), c.Action, '')), @query)
                    ORDER BY c.Time DESC"
                : "SELECT * FROM c ORDER BY c.Time DESC";

            var queryDefinition = new CosmosQueryDefinition(queryText);
            if (hasSearch)
            {
                queryDefinition.WithParameter("@query", normalizedSearch);
            }

            var normalizedContinuationToken = string.IsNullOrWhiteSpace(continuationToken) ? null : continuationToken;

            var iterator = _auditLogContainer.GetItemQueryIterator<JObject>(
                queryDefinition,
                normalizedContinuationToken,
                new QueryRequestOptions { MaxItemCount = pageSize });

            try
            {
                FeedResponse<JObject> response = await iterator.ReadNextAsync().ConfigureAwait(false);
                return new AuditLogPage
                {
                    Items = response.Take(pageSize).Select(CosmosLegacyDocumentMapper.ToAuditLog).ToList(),
                    ContinuationToken = response.ContinuationToken,
                    HasMore = !string.IsNullOrWhiteSpace(response.ContinuationToken)
                };
            }
            catch (CosmosException ex) when (normalizedContinuationToken != null && ex.StatusCode == HttpStatusCode.BadRequest)
            {
                // Invalid/expired continuation token — avoid surfacing as an opaque 500; client can treat as end of list.
                _logger.LogWarning(
                    ex,
                    "Audit log paged query returned BadRequest while using a continuation token; returning empty page. SubStatusCode={SubStatusCode}",
                    ex.SubStatusCode);
                return new AuditLogPage
                {
                    Items = new List<NewAuditLogEntry>(),
                    ContinuationToken = null,
                    HasMore = false
                };
            }
        }
    }
}
