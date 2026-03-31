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
    public interface IHomeRepository
    {
        Task<List<Home>> GetAllAsync();
        Task<Home> GetByIdAsync(Guid id);
        Task<List<Home>> GetByIdsAsync(List<Guid> ids);
        Task<Home> UpsertAsync(Home home);
    }

    public class CosmosHomeRepository : IHomeRepository
    {
        private readonly CosmosContainer _homesContainer;

        public CosmosHomeRepository(CosmosContainer homesContainer)
        {
            _homesContainer = homesContainer;
        }

        public async Task<List<Home>> GetAllAsync()
        {
            var iterator = _homesContainer.GetItemQueryIterator<JObject>(new CosmosQueryDefinition("SELECT * FROM c"));
            var results = new List<Home>();
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                results.AddRange(response.Select(CosmosLegacyDocumentMapper.ToHome));
            }

            return results;
        }

        public async Task<Home> GetByIdAsync(Guid id)
        {
            var doc = await GetRawHomeDocumentAsync(id);
            return doc == null ? null : CosmosLegacyDocumentMapper.ToHome(doc);
        }

        public async Task<List<Home>> GetByIdsAsync(List<Guid> ids)
        {
            var candidates = ids
                .SelectMany(id => new[] { CosmosLegacyDocumentMapper.ToHomeDocumentId(id), id.ToString("D") })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (candidates.Count == 0)
            {
                return new List<Home>();
            }

            var idLiterals = candidates.Select(x => $"\"{x}\"");
            var query = new CosmosQueryDefinition($"SELECT * FROM c WHERE c.id IN ({string.Join(", ", idLiterals)})");
            var iterator = _homesContainer.GetItemQueryIterator<JObject>(query);
            var results = new List<Home>();
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                results.AddRange(response.Select(CosmosLegacyDocumentMapper.ToHome));
            }

            return results;
        }

        public async Task<Home> UpsertAsync(Home home)
        {
            var existing = await GetRawHomeDocumentAsync(home.Id);
            JObject doc;
            if (existing != null)
            {
                CosmosLegacyDocumentMapper.MergeHomeIntoDocument(existing, home);
                StripCosmosSystemProperties(existing);
                doc = existing;
            }
            else
            {
                doc = CosmosLegacyDocumentMapper.ToHomeDocument(home);
            }

            var requestOptions = new ItemRequestOptions();
            if (!string.IsNullOrEmpty(home.ETag))
            {
                requestOptions.IfMatchEtag = home.ETag;
            }

            try
            {
                await _homesContainer.UpsertItemAsync(doc, CosmosPartitionKey.None, requestOptions);
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                throw new ConcurrencyConflictException(
                    $"Home {home.Id} was modified by another request. Retry the operation.", ex);
            }

            return home;
        }

        private async Task<JObject> GetRawHomeDocumentAsync(Guid id)
        {
            var candidates = new[] { CosmosLegacyDocumentMapper.ToHomeDocumentId(id), id.ToString("D") };
            var idLiterals = candidates.Select(x => $"\"{x}\"");
            var query = new CosmosQueryDefinition($"SELECT * FROM c WHERE c.id IN ({string.Join(", ", idLiterals)})");
            var iterator = _homesContainer.GetItemQueryIterator<JObject>(query);
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                var doc = response.FirstOrDefault();
                if (doc != null)
                {
                    return doc;
                }
            }

            return null;
        }

        private static void StripCosmosSystemProperties(JObject doc)
        {
            doc.Remove("_rid");
            doc.Remove("_self");
            doc.Remove("_etag");
            doc.Remove("_attachments");
            doc.Remove("_ts");
        }
    }
}
