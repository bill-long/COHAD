using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Net.Http.Headers;
using Web.Models;
using Web.Services.Repositories;

namespace Web.Services
{
    /// <summary>
    /// Caches the committee list in memory so repeated public reads avoid
    /// Cosmos DB round-trips. Admin writes invalidate the cache.
    /// </summary>
    public sealed class CommitteeListCache
    {
        private readonly ICommitteeRepository _committeeRepository;
        private readonly IMemoryCache _cache;
        private readonly JsonSerializerOptions _jsonOptions;

        private const string CommitteesCacheKey = "CommitteeListCache:Committees";
        internal const string CommitteesResponseKey = "CommitteeListCache:Response:Committees";
        private static readonly TimeSpan SlidingExpiration = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan AbsoluteExpiration = TimeSpan.FromMinutes(30);

        public CommitteeListCache(
            ICommitteeRepository committeeRepository,
            IMemoryCache cache,
            JsonSerializerOptions jsonOptions = null)
        {
            _committeeRepository = committeeRepository;
            _cache = cache;
            _jsonOptions = jsonOptions ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);
        }

        public async Task<List<Committee>> GetAllAsync()
        {
            if (_cache.TryGetValue(CommitteesCacheKey, out byte[] cachedJson))
            {
                return JsonSerializer.Deserialize<List<Committee>>(cachedJson, _jsonOptions);
            }

            var committees = await _committeeRepository.GetAllAsync();
            var json = JsonSerializer.SerializeToUtf8Bytes(committees, _jsonOptions);
            _cache.Set(CommitteesCacheKey, json, CacheOptions());
            return JsonSerializer.Deserialize<List<Committee>>(json, _jsonOptions);
        }

        /// <summary>Invalidate after any admin committee update.</summary>
        public void Invalidate()
        {
            _cache.Remove(CommitteesCacheKey);
            _cache.Remove(CommitteesResponseKey);
        }

        /// <summary>
        /// Returns a JSON response with <c>Cache-Control: public, no-cache</c> and
        /// a weak ETag derived from the serialized payload. If the client sends a
        /// matching <c>If-None-Match</c> header, a 304 Not Modified is returned.
        /// </summary>
        public IActionResult OkWithETag<T>(T payload, HttpRequest request, HttpResponse response,
            string responseCacheKey = null)
        {
            byte[] json;
            EntityTagHeaderValue etag;

            if (responseCacheKey != null &&
                _cache.TryGetValue(responseCacheKey, out CachedResponse cachedResponse))
            {
                json = cachedResponse.Json;
                etag = cachedResponse.ETag;
            }
            else
            {
                json = JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOptions);
                etag = new EntityTagHeaderValue(
                    $"\"{Convert.ToBase64String(SHA256.HashData(json))}\"", isWeak: true);

                if (responseCacheKey != null)
                {
                    _cache.Set(responseCacheKey, new CachedResponse(json, etag), CacheOptions());
                }
            }

            response.Headers.CacheControl = "public, no-cache";
            response.Headers.ETag = etag.ToString();

            if (request.GetTypedHeaders().IfNoneMatch
                    ?.Any(e => e.Compare(etag, useStrongComparison: false)) == true)
            {
                return new StatusCodeResult(StatusCodes.Status304NotModified);
            }

            return new FileContentResult(json, "application/json");
        }

        private static MemoryCacheEntryOptions CacheOptions()
        {
            return new MemoryCacheEntryOptions()
                .SetSlidingExpiration(SlidingExpiration)
                .SetAbsoluteExpiration(AbsoluteExpiration);
        }

        private sealed record CachedResponse(byte[] Json, EntityTagHeaderValue ETag);
    }
}
