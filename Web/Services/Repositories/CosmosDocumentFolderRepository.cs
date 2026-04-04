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
    public interface IDocumentFolderRepository
    {
        Task<List<DocumentFolder>> GetAllAsync();
        Task<DocumentFolder> GetByIdAsync(Guid id);
        Task<DocumentFolder> UpsertAsync(DocumentFolder folder);
        Task DeleteAsync(Guid id);
    }

    public class CosmosDocumentFolderRepository : IDocumentFolderRepository
    {
        private readonly CosmosContainer _container;

        public CosmosDocumentFolderRepository(CosmosContainer container)
        {
            _container = container;
        }

        public async Task<List<DocumentFolder>> GetAllAsync()
        {
            var query = new CosmosQueryDefinition("SELECT * FROM c WHERE STARTSWITH(c.id, 'DocumentFolder|')");
            var iterator = _container.GetItemQueryIterator<JObject>(query);
            var results = new List<DocumentFolder>();
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                results.AddRange(response.Select(CosmosLegacyDocumentMapper.ToDocumentFolder));
            }

            return results;
        }

        public async Task<DocumentFolder> GetByIdAsync(Guid id)
        {
            var query = new CosmosQueryDefinition("SELECT * FROM c WHERE c.id = @id")
                .WithParameter("@id", CosmosLegacyDocumentMapper.ToDocumentFolderId(id));
            var iterator = _container.GetItemQueryIterator<JObject>(query);
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                var doc = response.FirstOrDefault();
                if (doc != null)
                {
                    return CosmosLegacyDocumentMapper.ToDocumentFolder(doc);
                }
            }

            return null;
        }

        public async Task<DocumentFolder> UpsertAsync(DocumentFolder folder)
        {
            if (folder.Id == Guid.Empty)
            {
                folder.Id = Guid.NewGuid();
            }

            var doc = CosmosLegacyDocumentMapper.ToDocumentFolderDocument(folder);
            await _container.UpsertItemAsync(doc, CosmosPartitionKey.None);
            return folder;
        }

        public async Task DeleteAsync(Guid id)
        {
            var documentId = CosmosLegacyDocumentMapper.ToDocumentFolderId(id);
            try
            {
                await _container.DeleteItemAsync<JObject>(documentId, CosmosPartitionKey.None);
            }
            catch (Microsoft.Azure.Cosmos.CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Idempotent delete
            }
        }
    }
}
