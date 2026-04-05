using System;
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
    public class CosmosResidentRepository : IResidentRepository
    {
        private readonly CosmosContainer _container;

        public CosmosResidentRepository(CosmosContainer container)
        {
            _container = container;
        }

        public async Task<List<Resident>> GetAllAsync()
        {
            var iterator = _container.GetItemQueryIterator<JObject>(
                new CosmosQueryDefinition("SELECT * FROM c"));
            var results = new List<Resident>();
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                results.AddRange(response.Select(CosmosLegacyDocumentMapper.ToResidentEntity));
            }

            return results;
        }

        public async Task<Resident> GetByIdAsync(Guid id)
        {
            try
            {
                var docId = CosmosLegacyDocumentMapper.ResidentEntityDocumentId(id);
                var response = await _container.ReadItemAsync<JObject>(docId, CosmosPartitionKey.None);
                return CosmosLegacyDocumentMapper.ToResidentEntity(response.Resource);
            }
            catch (Microsoft.Azure.Cosmos.CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        public async Task<List<Resident>> GetByIdsAsync(IEnumerable<Guid> ids)
        {
            var idList = ids.ToList();
            if (idList.Count == 0)
            {
                return new List<Resident>();
            }

            // Parameterized IN clause to avoid injection.
            // Note: very large ID lists may hit Cosmos parameter-count or query-length limits.
            var query = new CosmosQueryDefinition("SELECT * FROM c WHERE c.id IN ("
                + string.Join(", ", idList.Select((_, i) => $"@id{i}")) + ")");
            for (var i = 0; i < idList.Count; i++)
            {
                query = query.WithParameter($"@id{i}", CosmosLegacyDocumentMapper.ResidentEntityDocumentId(idList[i]));
            }

            var iterator = _container.GetItemQueryIterator<JObject>(query);
            var results = new List<Resident>();
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                results.AddRange(response.Select(CosmosLegacyDocumentMapper.ToResidentEntity));
            }

            return results;
        }

        public async Task<List<Resident>> GetByHomeIdAsync(Guid homeId)
        {
            var query = new CosmosQueryDefinition(
                "SELECT * FROM c WHERE c.HomeId = @homeId")
                .WithParameter("@homeId", homeId.ToString("D"));
            var iterator = _container.GetItemQueryIterator<JObject>(query);
            var results = new List<Resident>();
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                results.AddRange(response.Select(CosmosLegacyDocumentMapper.ToResidentEntity));
            }

            return results;
        }

        public async Task<List<Resident>> GetByHomeIdsAsync(IEnumerable<Guid> homeIds)
        {
            var idSet = homeIds?.ToHashSet() ?? new HashSet<Guid>();
            if (idSet.Count == 0) return new List<Resident>();

            // Build a parameterized IN clause.
            var parameters = new CosmosQueryDefinition("SELECT * FROM c WHERE c.HomeId IN ("
                + string.Join(", ", idSet.Select((_, i) => $"@h{i}")) + ")");
            var idx = 0;
            foreach (var hid in idSet)
            {
                parameters = parameters.WithParameter($"@h{idx}", hid.ToString("D"));
                idx++;
            }

            var iterator = _container.GetItemQueryIterator<JObject>(parameters);
            var results = new List<Resident>();
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                results.AddRange(response.Select(CosmosLegacyDocumentMapper.ToResidentEntity));
            }

            return results;
        }

        public async Task<Resident> UpsertAsync(Resident resident)
        {
            var doc = CosmosLegacyDocumentMapper.ToResidentEntityDocument(resident);
            await _container.UpsertItemAsync(doc, CosmosPartitionKey.None);
            return resident;
        }

        public async Task DeleteAsync(Guid id)
        {
            try
            {
                var docId = CosmosLegacyDocumentMapper.ResidentEntityDocumentId(id);
                await _container.DeleteItemAsync<JObject>(docId, CosmosPartitionKey.None);
            }
            catch (Microsoft.Azure.Cosmos.CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // Already deleted or never existed — idempotent.
            }
        }

        public async Task DeleteByHomeIdAsync(Guid homeId)
        {
            var residents = await GetByHomeIdAsync(homeId);
            foreach (var r in residents)
            {
                await DeleteAsync(r.Id);
            }
        }
    }
}
