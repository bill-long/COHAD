using System;
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
    public interface IDocumentRepository
    {
        Task<List<ResidentDocument>> GetAllAsync();
        Task<ResidentDocument> GetByIdAsync(Guid id);
        Task<ResidentDocument> UpsertAsync(ResidentDocument document);
        Task DeleteAsync(Guid id);
    }

    public class CosmosDocumentRepository : IDocumentRepository
    {
        private readonly CosmosContainer _documentsContainer;

        public CosmosDocumentRepository(CosmosContainer documentsContainer)
        {
            _documentsContainer = documentsContainer;
        }

        public async Task<List<ResidentDocument>> GetAllAsync()
        {
            var query = new CosmosQueryDefinition("SELECT * FROM c WHERE STARTSWITH(c.id, 'ResidentDocument|')");
            var iterator = _documentsContainer.GetItemQueryIterator<JObject>(query);
            var results = new List<ResidentDocument>();
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                results.AddRange(response.Select(CosmosLegacyDocumentMapper.ToResidentDocument));
            }

            return results;
        }

        public async Task<ResidentDocument> GetByIdAsync(Guid id)
        {
            var query = new CosmosQueryDefinition("SELECT * FROM c WHERE c.id = @id").WithParameter(
                "@id",
                CosmosLegacyDocumentMapper.ToResidentDocumentId(id)
            );
            var iterator = _documentsContainer.GetItemQueryIterator<JObject>(query);
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                var doc = response.FirstOrDefault();
                if (doc != null)
                {
                    return CosmosLegacyDocumentMapper.ToResidentDocument(doc);
                }
            }

            return null;
        }

        public async Task<ResidentDocument> UpsertAsync(ResidentDocument document)
        {
            if (document.Id == Guid.Empty)
            {
                document.Id = Guid.NewGuid();
            }

            var doc = CosmosLegacyDocumentMapper.ToResidentDocumentDocument(document);
            await _documentsContainer.UpsertItemAsync(doc, CosmosPartitionKey.None);
            return document;
        }

        public async Task DeleteAsync(Guid id)
        {
            var documentId = CosmosLegacyDocumentMapper.ToResidentDocumentId(id);
            try
            {
                await _documentsContainer.DeleteItemAsync<JObject>(documentId, CosmosPartitionKey.None);
            }
            catch (Microsoft.Azure.Cosmos.CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Idempotent delete
            }
        }
    }
}
