using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Web.Services;

namespace Web.MockData
{
    public sealed class MockDocumentFileStore : IDocumentFileStore
    {
        private readonly Dictionary<string, StoredFile> _files = new();

        public Task UploadAsync(string blobPath, Stream stream, string contentType)
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            lock (_files)
            {
                _files[blobPath] = new StoredFile
                {
                    ContentType = contentType,
                    Bytes = ms.ToArray()
                };
            }

            return Task.CompletedTask;
        }

        public Task<DocumentFileResult> DownloadAsync(string blobPath)
        {
            lock (_files)
            {
                if (!_files.TryGetValue(blobPath, out var found))
                {
                    return Task.FromResult<DocumentFileResult>(null);
                }

                return Task.FromResult(new DocumentFileResult
                {
                    ContentType = found.ContentType,
                    Stream = new MemoryStream(found.Bytes, writable: false)
                });
            }
        }

        public Task DeleteAsync(string blobPath)
        {
            lock (_files)
            {
                _files.Remove(blobPath);
            }

            return Task.CompletedTask;
        }

        private class StoredFile
        {
            public string ContentType { get; set; }

            public byte[] Bytes { get; set; }
        }
    }
}
