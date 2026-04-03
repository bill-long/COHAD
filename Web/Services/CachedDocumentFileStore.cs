using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Net.Http.Headers;

namespace Web.Services
{
    /// <summary>
    /// Decorator around <see cref="IDocumentFileStore"/> that caches downloaded
    /// files in memory to avoid repeated blob storage round-trips for hot content.
    /// Files larger than <see cref="MaxCachedFileSizeBytes"/> are streamed through
    /// without buffering when their content length is known in advance; files with
    /// unknown content length may still be buffered before the cache decision.
    /// </summary>
    public sealed class CachedDocumentFileStore : IDocumentFileStore, IDisposable
    {
        private readonly IDocumentFileStore _inner;
        private readonly MemoryCache _cache;

        private const long MaxCachedFileSizeBytes = 10 * 1024 * 1024; // 10 MB per file
        private const long CacheSizeLimitBytes = 50 * 1024 * 1024;    // 50 MB total
        private static readonly TimeSpan SlidingExpiration = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan AbsoluteExpiration = TimeSpan.FromMinutes(60);

        public CachedDocumentFileStore(IDocumentFileStore inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = CacheSizeLimitBytes });
        }

        public Task UploadAsync(string blobPath, Stream stream, string contentType)
        {
            _cache.Remove(blobPath);
            return _inner.UploadAsync(blobPath, stream, contentType);
        }

        public async Task<DocumentFileResult> DownloadAsync(string blobPath)
        {
            if (_cache.TryGetValue(blobPath, out CachedFile cached))
            {
                return new DocumentFileResult
                {
                    Stream = new MemoryStream(cached.Bytes, writable: false),
                    ContentType = cached.ContentType,
                    EntityTag = cached.EntityTag,
                    LastModified = cached.LastModified,
                    ContentLength = cached.Bytes.Length
                };
            }

            var result = await _inner.DownloadAsync(blobPath);
            if (result == null)
            {
                return null;
            }

            // Skip buffering entirely for blobs known to exceed the per-file cache limit.
            if (result.ContentLength.HasValue && result.ContentLength.Value > MaxCachedFileSizeBytes)
            {
                return result;
            }

            // Buffer the stream so we can cache the bytes and still return a readable stream.
            using var ms = new MemoryStream();
            await using (result.Stream)
            {
                await result.Stream.CopyToAsync(ms);
            }
            var bytes = ms.ToArray();

            if (bytes.Length <= MaxCachedFileSizeBytes)
            {
                var entry = new CachedFile
                {
                    Bytes = bytes,
                    ContentType = result.ContentType,
                    EntityTag = result.EntityTag,
                    LastModified = result.LastModified
                };

                var options = new MemoryCacheEntryOptions()
                    .SetSlidingExpiration(SlidingExpiration)
                    .SetAbsoluteExpiration(AbsoluteExpiration)
                    .SetSize(bytes.Length);

                _cache.Set(blobPath, entry, options);
            }

            return new DocumentFileResult
            {
                Stream = new MemoryStream(bytes, writable: false),
                ContentType = result.ContentType,
                EntityTag = result.EntityTag,
                LastModified = result.LastModified,
                ContentLength = bytes.Length
            };
        }

        public Task DeleteAsync(string blobPath)
        {
            _cache.Remove(blobPath);
            return _inner.DeleteAsync(blobPath);
        }

        public void Dispose()
        {
            _cache.Dispose();
        }

        private class CachedFile
        {
            public byte[] Bytes { get; set; }
            public string ContentType { get; set; }
            public EntityTagHeaderValue EntityTag { get; set; }
            public DateTimeOffset? LastModified { get; set; }
        }
    }
}
