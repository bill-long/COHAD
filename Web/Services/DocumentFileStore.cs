using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Web.Configuration;

namespace Web.Services
{
    public class DocumentFileResult
    {
        public Stream Stream { get; set; }

        public string ContentType { get; set; }

        public EntityTagHeaderValue EntityTag { get; set; }

        public DateTimeOffset? LastModified { get; set; }

        /// <summary>
        /// Content length in bytes when known from the storage provider, or <c>null</c> if unavailable.
        /// </summary>
        public long? ContentLength { get; set; }
    }

    public interface IDocumentFileStore
    {
        Task UploadAsync(string blobPath, Stream stream, string contentType);
        Task<DocumentFileResult> DownloadAsync(string blobPath);
        Task DeleteAsync(string blobPath);
    }

    public class AzureBlobDocumentFileStore : IDocumentFileStore
    {
        private readonly BlobContainerClient _containerClient;
        private readonly SemaphoreSlim _containerInitLock = new(1, 1);
        private bool _containerEnsured;

        public AzureBlobDocumentFileStore(IOptions<DocumentStorageOptions> options)
        {
            var value = options?.Value ?? throw new InvalidOperationException("DocumentStorage configuration is missing.");
            if (string.IsNullOrWhiteSpace(value.ConnectionString))
            {
                throw new InvalidOperationException("DocumentStorage:ConnectionString must be configured.");
            }

            if (string.IsNullOrWhiteSpace(value.ContainerName))
            {
                throw new InvalidOperationException("DocumentStorage:ContainerName must be configured.");
            }

            var serviceClient = new BlobServiceClient(value.ConnectionString);
            _containerClient = serviceClient.GetBlobContainerClient(value.ContainerName);
        }

        private async Task EnsureContainerAsync()
        {
            if (_containerEnsured)
            {
                return;
            }

            await _containerInitLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_containerEnsured)
                {
                    return;
                }

                await _containerClient.CreateIfNotExistsAsync(PublicAccessType.None).ConfigureAwait(false);
                _containerEnsured = true;
            }
            finally
            {
                _containerInitLock.Release();
            }
        }

        public async Task UploadAsync(string blobPath, Stream stream, string contentType)
        {
            await EnsureContainerAsync().ConfigureAwait(false);
            var blob = _containerClient.GetBlobClient(blobPath);
            await blob.UploadAsync(stream, new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders
                {
                    ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
                    CacheControl = "public, no-cache"
                }
            });
        }

        public async Task<DocumentFileResult> DownloadAsync(string blobPath)
        {
            await EnsureContainerAsync().ConfigureAwait(false);
            var blob = _containerClient.GetBlobClient(blobPath);
            var exists = await blob.ExistsAsync();
            if (!exists.Value)
            {
                return null;
            }

            var response = await blob.DownloadStreamingAsync();
            var etagString = response.Value.Details.ETag.ToString("H");
            EntityTagHeaderValue.TryParse(etagString, out var entityTag);
            return new DocumentFileResult
            {
                Stream = response.Value.Content,
                ContentType = response.Value.Details.ContentType,
                EntityTag = entityTag,
                LastModified = response.Value.Details.LastModified,
                ContentLength = response.Value.Details.ContentLength
            };
        }

        public async Task DeleteAsync(string blobPath)
        {
            await EnsureContainerAsync().ConfigureAwait(false);
            var blob = _containerClient.GetBlobClient(blobPath);
            await blob.DeleteIfExistsAsync();
        }
    }
}
