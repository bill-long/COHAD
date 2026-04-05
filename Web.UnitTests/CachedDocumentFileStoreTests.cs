using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Net.Http.Headers;
using Web.Services;

namespace Web.UnitTests;

public sealed class CachedDocumentFileStoreTests : IDisposable
{
    private readonly FakeDocumentFileStore _inner = new();
    private readonly CachedDocumentFileStore _sut;

    public CachedDocumentFileStoreTests()
    {
        _sut = new CachedDocumentFileStore(_inner);
    }

    public void Dispose() => _sut.Dispose();

    [Fact]
    public async Task DownloadAsync_returns_null_when_blob_missing()
    {
        var result = await _sut.DownloadAsync("does-not-exist");
        Assert.Null(result);
    }

    [Fact]
    public async Task DownloadAsync_returns_content_on_cache_miss()
    {
        _inner.Seed("img/test.jpg", "image/jpeg", "hello");

        var result = await _sut.DownloadAsync("img/test.jpg");

        Assert.NotNull(result);
        Assert.Equal("image/jpeg", result.ContentType);
        Assert.Equal("hello", await ReadStream(result.Stream));
    }

    [Fact]
    public async Task DownloadAsync_serves_from_cache_on_second_call()
    {
        _inner.Seed("img/test.jpg", "image/jpeg", "hello");

        // First call — cache miss
        var first = await _sut.DownloadAsync("img/test.jpg");
        await ReadStream(first.Stream);

        // Remove from inner store to prove second call uses cache
        _inner.Clear();

        // Second call — cache hit
        var second = await _sut.DownloadAsync("img/test.jpg");
        Assert.NotNull(second);
        Assert.Equal("hello", await ReadStream(second.Stream));
    }

    [Fact]
    public async Task DownloadAsync_preserves_etag_and_last_modified()
    {
        var etag = new EntityTagHeaderValue("\"abc123\"");
        var lastMod = new DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.Zero);
        _inner.Seed("img/test.jpg", "image/jpeg", "data", etag, lastMod);

        var result = await _sut.DownloadAsync("img/test.jpg");

        Assert.Equal(etag, result.EntityTag);
        Assert.Equal(lastMod, result.LastModified);
    }

    [Fact]
    public async Task UploadAsync_invalidates_cache()
    {
        _inner.Seed("img/test.jpg", "image/jpeg", "original");

        // Populate cache
        var first = await _sut.DownloadAsync("img/test.jpg");
        await ReadStream(first.Stream);

        // Upload replaces the blob
        using var newStream = new MemoryStream(Encoding.UTF8.GetBytes("updated"));
        await _sut.UploadAsync("img/test.jpg", newStream, "image/jpeg");

        // Next download should return updated content (cache was invalidated)
        var second = await _sut.DownloadAsync("img/test.jpg");
        Assert.Equal("updated", await ReadStream(second.Stream));
    }

    [Fact]
    public async Task DeleteAsync_invalidates_cache()
    {
        _inner.Seed("img/test.jpg", "image/jpeg", "hello");

        // Populate cache
        var first = await _sut.DownloadAsync("img/test.jpg");
        await ReadStream(first.Stream);

        // Delete removes from both cache and inner store
        await _sut.DeleteAsync("img/test.jpg");

        var second = await _sut.DownloadAsync("img/test.jpg");
        Assert.Null(second);
    }

    [Fact]
    public async Task DownloadAsync_skips_buffering_for_large_blobs()
    {
        // Seed a blob with ContentLength > 10 MB
        var largeContentLength = 11 * 1024 * 1024L;
        _inner.SeedWithContentLength("docs/large.pdf", "application/pdf", "small-body", largeContentLength);

        var result = await _sut.DownloadAsync("docs/large.pdf");

        Assert.NotNull(result);
        // The stream should be the original (not buffered into a new MemoryStream)
        Assert.IsType<FakeDocumentFileStore.TaggedStream>(result.Stream);

        // Remove from inner — second call should NOT be cached
        _inner.Clear();
        var second = await _sut.DownloadAsync("docs/large.pdf");
        Assert.Null(second);
    }

    [Fact]
    public async Task DownloadAsync_sets_content_length_on_cached_result()
    {
        _inner.Seed("img/test.jpg", "image/jpeg", "hello");

        // First call — miss
        var first = await _sut.DownloadAsync("img/test.jpg");
        Assert.Equal(5, first.ContentLength);

        // Second call — hit
        _inner.Clear();
        var second = await _sut.DownloadAsync("img/test.jpg");
        Assert.Equal(5, second.ContentLength);
    }

    private static async Task<string> ReadStream(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    /// <summary>
    /// Minimal in-memory <see cref="IDocumentFileStore"/> for testing the caching decorator.
    /// </summary>
    private sealed class FakeDocumentFileStore : IDocumentFileStore
    {
        private readonly Dictionary<string, FakeBlob> _blobs = new();

        public void Seed(
            string path,
            string contentType,
            string content,
            EntityTagHeaderValue etag = null,
            DateTimeOffset? lastModified = null
        )
        {
            _blobs[path] = new FakeBlob
            {
                ContentType = contentType,
                Bytes = Encoding.UTF8.GetBytes(content),
                EntityTag = etag,
                LastModified = lastModified,
                ContentLength = null, // will be set from Bytes.Length in DownloadAsync
            };
        }

        public void SeedWithContentLength(string path, string contentType, string content, long contentLength)
        {
            _blobs[path] = new FakeBlob
            {
                ContentType = contentType,
                Bytes = Encoding.UTF8.GetBytes(content),
                ContentLength = contentLength,
            };
        }

        public void Clear() => _blobs.Clear();

        public Task UploadAsync(string blobPath, Stream stream, string contentType)
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            _blobs[blobPath] = new FakeBlob
            {
                ContentType = contentType,
                Bytes = ms.ToArray(),
                ContentLength = null,
            };
            return Task.CompletedTask;
        }

        public Task<DocumentFileResult> DownloadAsync(string blobPath)
        {
            if (!_blobs.TryGetValue(blobPath, out var blob))
                return Task.FromResult<DocumentFileResult>(null);

            return Task.FromResult(
                new DocumentFileResult
                {
                    Stream = new TaggedStream(blob.Bytes),
                    ContentType = blob.ContentType,
                    EntityTag = blob.EntityTag,
                    LastModified = blob.LastModified,
                    ContentLength = blob.ContentLength ?? blob.Bytes.Length,
                }
            );
        }

        public Task DeleteAsync(string blobPath)
        {
            _blobs.Remove(blobPath);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Custom stream type so tests can assert when the original (non-buffered) stream is returned.
        /// </summary>
        public sealed class TaggedStream : MemoryStream
        {
            public TaggedStream(byte[] buffer)
                : base(buffer, writable: false) { }
        }

        private class FakeBlob
        {
            public string ContentType { get; set; }
            public byte[] Bytes { get; set; }
            public EntityTagHeaderValue EntityTag { get; set; }
            public DateTimeOffset? LastModified { get; set; }
            public long? ContentLength { get; set; }
        }
    }
}
