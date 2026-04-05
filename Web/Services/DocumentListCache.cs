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
    /// Caches the document and folder list queries in memory so repeated
    /// reads from authenticated users avoid Cosmos DB round-trips.
    /// Write operations (upload, delete, folder CRUD) invalidate the
    /// relevant cache entries for the current application instance only.
    /// Because <see cref="IMemoryCache" /> is per-process, scaled-out
    /// deployments can still serve stale cached data from other instances
    /// until those entries expire or a distributed invalidation strategy is used.
    /// </summary>
    public sealed class DocumentListCache
    {
        private readonly IDocumentRepository _documentRepository;
        private readonly IDocumentFolderRepository _folderRepository;
        private readonly IMemoryCache _cache;
        private readonly JsonSerializerOptions _jsonOptions;

        private const string DocumentsCacheKey = "DocumentListCache:Documents";
        private const string FoldersCacheKey = "DocumentListCache:Folders";
        internal const string DocumentsResponseKey = "DocumentListCache:Response:Documents";
        internal const string FoldersResponseKey = "DocumentListCache:Response:Folders";
        private static readonly TimeSpan SlidingExpiration = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan AbsoluteExpiration = TimeSpan.FromMinutes(30);

        public DocumentListCache(
            IDocumentRepository documentRepository,
            IDocumentFolderRepository folderRepository,
            IMemoryCache cache,
            JsonSerializerOptions jsonOptions = null
        )
        {
            _documentRepository = documentRepository;
            _folderRepository = folderRepository;
            _cache = cache;
            _jsonOptions = jsonOptions ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);
        }

        public async Task<List<ResidentDocument>> GetAllDocumentsAsync()
        {
            if (_cache.TryGetValue(DocumentsCacheKey, out List<ResidentDocument> cached))
            {
                return new List<ResidentDocument>(cached);
            }

            var docs = await _documentRepository.GetAllAsync();
            _cache.Set(DocumentsCacheKey, docs, CacheOptions());
            return new List<ResidentDocument>(docs);
        }

        public async Task<List<DocumentFolder>> GetAllFoldersAsync()
        {
            if (_cache.TryGetValue(FoldersCacheKey, out List<DocumentFolder> cached))
            {
                return new List<DocumentFolder>(cached);
            }

            var folders = await _folderRepository.GetAllAsync();
            _cache.Set(FoldersCacheKey, folders, CacheOptions());
            return new List<DocumentFolder>(folders);
        }

        /// <summary>Invalidate after document upload or delete.</summary>
        public void InvalidateDocuments()
        {
            _cache.Remove(DocumentsCacheKey);
            _cache.Remove(DocumentsResponseKey);
            // Folder counts depend on documents, so invalidate folders too.
            _cache.Remove(FoldersCacheKey);
            _cache.Remove(FoldersResponseKey);
        }

        /// <summary>Invalidate after folder create, update, delete, or reorder.</summary>
        public void InvalidateFolders()
        {
            _cache.Remove(FoldersCacheKey);
            _cache.Remove(FoldersResponseKey);
            // Document summaries include folder names, so invalidate documents too.
            _cache.Remove(DocumentsCacheKey);
            _cache.Remove(DocumentsResponseKey);
        }

        /// <summary>
        /// Returns a JSON response with <c>Cache-Control: private, no-cache</c> and
        /// a weak ETag derived from the serialized payload. If the client sends a
        /// matching <c>If-None-Match</c> header, a 304 Not Modified is returned.
        /// </summary>
        /// <param name="payload">The object to serialize as the response body.</param>
        /// <param name="request">The current HTTP request (for If-None-Match header).</param>
        /// <param name="response">The current HTTP response (for Cache-Control/ETag headers).</param>
        /// <param name="responseCacheKey">
        /// Optional cache key for the serialized bytes and ETag. When provided, subsequent
        /// calls with the same key skip JSON serialization and SHA-256 hashing entirely
        /// until the entry is evicted. Pass <c>null</c> to always serialize fresh.
        /// </param>
        public IActionResult OkWithETag<T>(
            T payload,
            HttpRequest request,
            HttpResponse response,
            string responseCacheKey = null
        )
        {
            byte[] json;
            EntityTagHeaderValue etag;

            if (responseCacheKey != null && _cache.TryGetValue(responseCacheKey, out CachedResponse cachedResponse))
            {
                json = cachedResponse.Json;
                etag = cachedResponse.ETag;
            }
            else
            {
                json = JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOptions);
                etag = new EntityTagHeaderValue($"\"{Convert.ToBase64String(SHA256.HashData(json))}\"", isWeak: true);

                if (responseCacheKey != null)
                {
                    _cache.Set(responseCacheKey, new CachedResponse(json, etag), CacheOptions());
                }
            }

            response.Headers.CacheControl = "private, no-cache";
            response.Headers.ETag = etag.ToString();

            if (request.GetTypedHeaders().IfNoneMatch?.Any(e => e.Compare(etag, useStrongComparison: false)) == true)
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
