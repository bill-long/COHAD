using System;
using System.IO;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;
using Web.Configuration;

namespace Web.Services
{
    public class DocumentFileResult
    {
        public Stream Stream { get; set; }

        public string ContentType { get; set; }
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
            _containerClient.CreateIfNotExists(PublicAccessType.None);
        }

        public async Task UploadAsync(string blobPath, Stream stream, string contentType)
        {
            var blob = _containerClient.GetBlobClient(blobPath);
            await blob.UploadAsync(stream, new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders
                {
                    ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType
                }
            });
        }

        public async Task<DocumentFileResult> DownloadAsync(string blobPath)
        {
            var blob = _containerClient.GetBlobClient(blobPath);
            var exists = await blob.ExistsAsync();
            if (!exists.Value)
            {
                return null;
            }

            var response = await blob.DownloadStreamingAsync();
            return new DocumentFileResult
            {
                Stream = response.Value.Content,
                ContentType = response.Value.Details.ContentType
            };
        }

        public async Task DeleteAsync(string blobPath)
        {
            var blob = _containerClient.GetBlobClient(blobPath);
            await blob.DeleteIfExistsAsync();
        }
    }
}
