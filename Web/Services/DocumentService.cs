using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage.Blob;

namespace Web.Services
{
    public class DocumentService
    {
        private readonly CloudBlobContainer _container;

        public DocumentService(CloudBlobContainer container)
        {
            _container = container;
        }

        public async Task<Node> GetHierarchy()
        {
            BlobContinuationToken continuationToken = null;
            var results = new List<IListBlobItem>();
            do
            {
                var response = await _container.ListBlobsSegmentedAsync(prefix: null, useFlatBlobListing: true,
                    blobListingDetails: BlobListingDetails.None, maxResults: null, currentToken: continuationToken,
                    options: null, operationContext: null);
                continuationToken = response.ContinuationToken;
                results.AddRange(response.Results);
            }
            while (continuationToken != null);

            var topLevel = new Node { Name = "" };

            foreach (var result in results)
            {
                if (!(result is CloudBlockBlob blob)) continue;

                var pathSplit = blob.Name.Split('/');
                topLevel.RecursiveAddIfNotExists(pathSplit);
            }

            return topLevel;
        }

        public async Task<(Stream, string)> GetDocument(string path)
        {
            var memoryStream = new MemoryStream();
            var blob = _container.GetBlobReference(path);
            await blob.DownloadToStreamAsync(memoryStream);
            return (memoryStream, blob.Properties.ContentType);
        }
    }

    public class Node
    {
        public string Name { get; set; }
        public List<Node> Children { get; set; }

        public void RecursiveAddIfNotExists(string[] nodePath)
        {
            if (Children == null)
            {
                Children = new List<Node>();
            }

            var matchingNode = Children.FirstOrDefault(n => n.Name == nodePath.First());
            if (matchingNode == null)
            {
                matchingNode = new Node { Name = nodePath.First() };
                Children.Add(matchingNode);
            }

            if (nodePath.Length > 1)
            {
                matchingNode.RecursiveAddIfNotExists(nodePath.Skip(1).ToArray());
            }
        }
    }
}
