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
    public class CosmosEmailJobRepository : IEmailJobRepository
    {
        private readonly CosmosContainer _emailJobContainer;

        public CosmosEmailJobRepository(CosmosContainer emailJobContainer)
        {
            _emailJobContainer = emailJobContainer;
        }

        public async Task AddAsync(EmailJob job)
        {
            var doc = CosmosLegacyDocumentMapper.ToEmailJobDocument(job);
            await _emailJobContainer.CreateItemAsync(doc, CosmosPartitionKey.None);
        }

        public async Task<EmailJob> GetByIdAsync(Guid jobId)
        {
            var documentId = CosmosLegacyDocumentMapper.ToEmailJobDocumentId(jobId);
            try
            {
                var response = await _emailJobContainer.ReadItemAsync<JObject>(documentId, CosmosPartitionKey.None);
                return CosmosLegacyDocumentMapper.ToEmailJob(response.Resource);
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        public async Task UpdateAsync(EmailJob job)
        {
            var doc = CosmosLegacyDocumentMapper.ToEmailJobDocument(job);
            var documentId = doc.Value<string>("id");
            await _emailJobContainer.ReplaceItemAsync(doc, documentId, CosmosPartitionKey.None);
        }

        public async Task<List<EmailJob>> GetIncompleteJobsAsync()
        {
            var query = new CosmosQueryDefinition(
                "SELECT * FROM c WHERE c.Status IN (@queued, @inProgress) ORDER BY c.CreatedUtc ASC")
                .WithParameter("@queued", nameof(EmailJobStatus.Queued))
                .WithParameter("@inProgress", nameof(EmailJobStatus.InProgress));

            var iterator = _emailJobContainer.GetItemQueryIterator<JObject>(query);
            var results = new List<EmailJob>();
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                results.AddRange(response.Select(CosmosLegacyDocumentMapper.ToEmailJob));
            }

            return results;
        }

        public async Task<List<EmailJob>> GetRecentJobsAsync(int limit)
        {
            var query = new CosmosQueryDefinition(
                "SELECT TOP @limit * FROM c ORDER BY c.CreatedUtc DESC")
                .WithParameter("@limit", limit);

            var iterator = _emailJobContainer.GetItemQueryIterator<JObject>(query);
            var results = new List<EmailJob>();
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                results.AddRange(response.Select(CosmosLegacyDocumentMapper.ToEmailJob));
            }

            return results;
        }
    }
}
