using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Web.Models;
using CosmosContainer = Microsoft.Azure.Cosmos.Container;
using CosmosPartitionKey = Microsoft.Azure.Cosmos.PartitionKey;
using CosmosQueryDefinition = Microsoft.Azure.Cosmos.QueryDefinition;

namespace Web.Services.Repositories
{
    public interface IBlogPostRepository
    {
        Task<List<BlogPost>> GetAllAsync();

        /// <summary>
        /// Published posts with <see cref="BlogPost.PublishUtc"/> &lt;= <paramref name="asOfUtc"/>, newest first.
        /// </summary>
        Task<List<BlogPost>> GetPublishedAsync(DateTime asOfUtc);

        Task<BlogPost> GetByIdAsync(Guid id);

        Task<BlogPost> GetByRouteSegmentAsync(string segment);

        Task<BlogPostReadResult> ReadAsync(Guid id);

        Task<BlogPost> UpsertAsync(BlogPost blogPost);

        Task<BlogPost> ReplaceAsync(BlogPost blogPost, string ifMatchEtag);

        Task DeleteAsync(Guid id);
    }

    public class BlogPostReadResult
    {
        public BlogPost Post { get; set; }

        public string ETag { get; set; }
    }

    public class CosmosBlogPostRepository : IBlogPostRepository
    {
        private readonly CosmosContainer _container;

        public CosmosBlogPostRepository(CosmosContainer container)
        {
            _container = container;
        }

        public async Task<List<BlogPost>> GetAllAsync()
        {
            var iterator = _container.GetItemQueryIterator<BlogPost>(new CosmosQueryDefinition("SELECT * FROM c"));
            var results = new List<BlogPost>();
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                results.AddRange(response);
            }

            return results;
        }

        public async Task<List<BlogPost>> GetPublishedAsync(DateTime asOfUtc)
        {
            var query = new CosmosQueryDefinition("SELECT * FROM c WHERE c.PublishUtc <= @asOf ORDER BY c.PublishUtc DESC")
                .WithParameter("@asOf", asOfUtc);
            var iterator = _container.GetItemQueryIterator<BlogPost>(query);
            var results = new List<BlogPost>();
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                results.AddRange(response);
            }

            return results;
        }

        public async Task<BlogPost> GetByIdAsync(Guid id)
        {
            try
            {
                var response = await _container.ReadItemAsync<BlogPost>(id.ToString("D"), CosmosPartitionKey.None);
                return response.Resource;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        public async Task<BlogPost> GetByRouteSegmentAsync(string segment)
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                return null;
            }

            if (Guid.TryParse(segment, out var guid))
            {
                return await GetByIdAsync(guid);
            }

            var query = new CosmosQueryDefinition("SELECT * FROM c WHERE LOWER(c.PublicSlug) = LOWER(@slug)")
                .WithParameter("@slug", segment);
            var iterator = _container.GetItemQueryIterator<BlogPost>(query);
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                var doc = response.FirstOrDefault();
                if (doc != null)
                {
                    return doc;
                }
            }

            return null;
        }

        public async Task<BlogPostReadResult> ReadAsync(Guid id)
        {
            try
            {
                var response = await _container.ReadItemAsync<BlogPost>(id.ToString("D"), CosmosPartitionKey.None);
                return new BlogPostReadResult
                {
                    Post = response.Resource,
                    ETag = response.ETag
                };
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        public async Task<BlogPost> UpsertAsync(BlogPost blogPost)
        {
            if (blogPost.Id == Guid.Empty)
            {
                blogPost.Id = Guid.NewGuid();
            }

            var response = await _container.UpsertItemAsync(blogPost, CosmosPartitionKey.None);
            return response.Resource;
        }

        public async Task<BlogPost> ReplaceAsync(BlogPost blogPost, string ifMatchEtag)
        {
            var options = new ItemRequestOptions { IfMatchEtag = ifMatchEtag };
            var response = await _container.ReplaceItemAsync(
                blogPost,
                blogPost.Id.ToString("D"),
                CosmosPartitionKey.None,
                options);
            return response.Resource;
        }

        public async Task DeleteAsync(Guid id)
        {
            try
            {
                await _container.DeleteItemAsync<BlogPost>(id.ToString("D"), CosmosPartitionKey.None);
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // Idempotent delete
            }
        }
    }
}
