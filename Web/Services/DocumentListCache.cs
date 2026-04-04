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
    /// relevant cache entries.
    /// </summary>
    public sealed class DocumentListCache
    {
        private readonly IDocumentRepository _documentRepository;
        private readonly IDocumentFolderRepository _folderRepository;
        private readonly IMemoryCache _cache;
        private readonly JsonSerializerOptions _jsonOptions;

        private const string DocumentsCacheKey = "DocumentListCache:Documents";
        private const string FoldersCacheKey = "DocumentListCache:Folders";
        private static readonly TimeSpan SlidingExpiration = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan AbsoluteExpiration = TimeSpan.FromMinutes(30);

        public DocumentListCache(
            IDocumentRepository documentRepository,
            IDocumentFolderRepository folderRepository,
            IMemoryCache cache,
            JsonSerializerOptions jsonOptions = null)
        {
            _documentRepository = documentRepository;
            _folderRepository = folderRepository;
            _cache = cache;
            _jsonOptions = jsonOptions;
        }

        public async Task<List<ResidentDocument>> GetAllDocumentsAsync()
        {
            if (_cache.TryGetValue(DocumentsCacheKey, out List<ResidentDocument> cached))
            {
                return cached;
            }

            var docs = await _documentRepository.GetAllAsync();
            _cache.Set(DocumentsCacheKey, docs, CacheOptions());
            return docs;
        }

        public async Task<List<DocumentFolder>> GetAllFoldersAsync()
        {
            if (_cache.TryGetValue(FoldersCacheKey, out List<DocumentFolder> cached))
            {
                return cached;
            }

            var folders = await _folderRepository.GetAllAsync();
            _cache.Set(FoldersCacheKey, folders, CacheOptions());
            return folders;
        }

        /// <summary>Invalidate after document upload or delete.</summary>
        public void InvalidateDocuments()
        {
            _cache.Remove(DocumentsCacheKey);
            // Folder counts depend on documents, so invalidate folders too.
            _cache.Remove(FoldersCacheKey);
        }

        /// <summary>Invalidate after folder create, update, delete, or reorder.</summary>
        public void InvalidateFolders()
        {
            _cache.Remove(FoldersCacheKey);
            // Document summaries include folder names, so invalidate documents too.
            _cache.Remove(DocumentsCacheKey);
        }

        /// <summary>
        /// Returns a JSON response with <c>Cache-Control: private, no-cache</c> and
        /// a weak ETag derived from the serialized payload. If the client sends a
        /// matching <c>If-None-Match</c> header, a 304 Not Modified is returned.
        /// </summary>
        public IActionResult OkWithETag<T>(T payload, HttpRequest request, HttpResponse response)
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(payload, _jsonOptions);
            var etag = new EntityTagHeaderValue(
                $"\"{Convert.ToBase64String(SHA256.HashData(json))}\"", isWeak: true);

            response.Headers.CacheControl = "private, no-cache";
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
    }
}
