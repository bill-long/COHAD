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
    public interface IAuditLogRepository
    {
        Task AddAsync(NewAuditLogEntry entry);
        Task<List<NewAuditLogEntry>> GetAllAsync();
    }

    public class CosmosAuditLogRepository : IAuditLogRepository
    {
        private readonly CosmosContainer _auditLogContainer;

        public CosmosAuditLogRepository(CosmosContainer auditLogContainer)
        {
            _auditLogContainer = auditLogContainer;
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
    }
}
