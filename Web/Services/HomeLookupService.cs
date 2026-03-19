using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Web.Models;
using CosmosContainer = Microsoft.Azure.Cosmos.Container;
using CosmosQueryDefinition = Microsoft.Azure.Cosmos.QueryDefinition;

namespace Web.Services
{
    public interface IHomeLookupService
    {
        Task<List<Home>> LoadOwnedHomesAsync(List<Guid> ownedHomeIds);
    }

    public class HomeLookupService : IHomeLookupService
    {
        private readonly CosmosContainer _homesContainer;
        private readonly ILogger<HomeLookupService> _logger;

        public HomeLookupService(CosmosContainer homesContainer, ILogger<HomeLookupService> logger)
        {
            _homesContainer = homesContainer;
            _logger = logger;
        }

        public async Task<List<Home>> LoadOwnedHomesAsync(List<Guid> ownedHomeIds)
        {
            var candidateIds = ownedHomeIds
                .SelectMany(id => new[] { $"Home|{id:D}", id.ToString("D") })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (candidateIds.Count == 0)
            {
                return new List<Home>();
            }

            var idLiterals = candidateIds.Select(id => $"\"{id}\"");
            var queryText = $"SELECT * FROM c WHERE c.id IN ({string.Join(", ", idLiterals)})";
            var iterator = _homesContainer.GetItemQueryIterator<HomeDocument>(new CosmosQueryDefinition(queryText));

            var results = new List<Home>();
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                foreach (var doc in response)
                {
                    if (!TryParseHomeGuid(doc.Id, out var parsedId))
                    {
                        continue;
                    }

                    results.Add(new Home
                    {
                        Id = parsedId,
                        StreetNumber = doc.StreetNumber,
                        StreetName = doc.StreetName,
                        PhoneNumber = DeserializeFlexible<PhoneNumber>(doc.PhoneNumber),
                        EmailAddress = DeserializeFlexible<EmailAddress>(doc.EmailAddress),
                        Residents = DeserializeFlexible<List<Resident>>(doc.Residents) ?? new List<Resident>()
                    });
                }
            }

            _logger.LogInformation(
                "Home lookup via Cosmos SDK. requested={RequestedCount} matched={MatchedCount}",
                candidateIds.Count,
                results.Count);

            return results;
        }

        private static bool TryParseHomeGuid(string rawId, out Guid parsedId)
        {
            if (Guid.TryParse(rawId, out parsedId))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(rawId))
            {
                var split = rawId.Split('|', StringSplitOptions.RemoveEmptyEntries);
                var tail = split.Length > 0 ? split[split.Length - 1] : rawId;
                if (Guid.TryParse(tail, out parsedId))
                {
                    return true;
                }
            }

            parsedId = Guid.Empty;
            return false;
        }

        private static T DeserializeFlexible<T>(object raw)
        {
            if (raw == null)
            {
                return default;
            }

            if (raw is string rawString)
            {
                if (string.IsNullOrWhiteSpace(rawString))
                {
                    return default;
                }

                return JsonConvert.DeserializeObject<T>(rawString);
            }

            if (raw is JToken token)
            {
                if (token.Type == JTokenType.Null)
                {
                    return default;
                }

                if (token.Type == JTokenType.String)
                {
                    var tokenString = token.Value<string>();
                    return string.IsNullOrWhiteSpace(tokenString)
                        ? default
                        : JsonConvert.DeserializeObject<T>(tokenString);
                }

                return token.ToObject<T>();
            }

            return JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(raw));
        }

        private sealed class HomeDocument
        {
            [JsonPropertyName("id")]
            public string Id { get; set; }
            public int StreetNumber { get; set; }
            public string StreetName { get; set; }
            public object PhoneNumber { get; set; }
            public object EmailAddress { get; set; }
            public object Residents { get; set; }
        }
    }
}
