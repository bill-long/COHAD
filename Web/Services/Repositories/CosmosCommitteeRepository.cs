using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Web.Models;
using Web.Services.Cosmos;
using CosmosContainer = Microsoft.Azure.Cosmos.Container;
using CosmosPartitionKey = Microsoft.Azure.Cosmos.PartitionKey;
using CosmosQueryDefinition = Microsoft.Azure.Cosmos.QueryDefinition;

namespace Web.Services.Repositories
{
    public interface ICommitteeRepository
    {
        Task<List<Committee>> GetAllAsync();

        Task<Committee> GetByIdAsync(string committeeKey);

        Task<Committee> UpsertAsync(Committee committee);
    }

    public class CosmosCommitteeRepository : ICommitteeRepository
    {
        private readonly CosmosContainer _container;

        public CosmosCommitteeRepository(CosmosContainer container)
        {
            _container = container;
        }

        public async Task<List<Committee>> GetAllAsync()
        {
            var iterator = _container.GetItemQueryIterator<JObject>(new CosmosQueryDefinition("SELECT * FROM c"));
            var results = new List<Committee>();
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                results.AddRange(response.Select(CosmosLegacyDocumentMapper.ToCommittee));
            }

            return results;
        }

        public async Task<Committee> GetByIdAsync(string committeeKey)
        {
            try
            {
                var response = await _container.ReadItemAsync<JObject>(committeeKey, CosmosPartitionKey.None);
                return CosmosLegacyDocumentMapper.ToCommittee(response.Resource);
            }
            catch (Microsoft.Azure.Cosmos.CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        public async Task<Committee> UpsertAsync(Committee committee)
        {
            var doc = CosmosLegacyDocumentMapper.ToCommitteeDocument(committee);
            await _container.UpsertItemAsync(doc, CosmosPartitionKey.None);
            return committee;
        }
    }
}
