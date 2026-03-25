using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Web.Models;
using CosmosContainer = Microsoft.Azure.Cosmos.Container;
using CosmosPartitionKey = Microsoft.Azure.Cosmos.PartitionKey;
using CosmosQueryDefinition = Microsoft.Azure.Cosmos.QueryDefinition;

namespace Web.Services.Repositories
{
    public interface IBlogCommentRepository
    {
        Task<List<BlogComment>> GetByBlogPostIdAsync(Guid blogPostId);
        Task<BlogComment> GetByIdAsync(Guid commentId);
        Task<BlogComment> UpsertAsync(BlogComment comment);
        Task DeleteAsync(Guid commentId);
        Task DeleteByBlogPostCascadeAsync(Guid blogPostId);
    }

    public class CosmosBlogCommentRepository : IBlogCommentRepository
    {
        private readonly CosmosContainer _container;

        public CosmosBlogCommentRepository(CosmosContainer container)
        {
            _container = container;
        }

        public async Task<List<BlogComment>> GetByBlogPostIdAsync(Guid blogPostId)
        {
            var query = new CosmosQueryDefinition("SELECT * FROM c WHERE c.BlogPostId = @blogPostId ORDER BY c.CreatedUtc ASC")
                .WithParameter("@blogPostId", blogPostId.ToString("D"));
            var iterator = _container.GetItemQueryIterator<JObject>(query);
            var results = new List<BlogComment>();
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                results.AddRange(response.Select(ToBlogComment));
            }

            return results;
        }

        public async Task<BlogComment> GetByIdAsync(Guid commentId)
        {
            var query = new CosmosQueryDefinition("SELECT * FROM c WHERE c.id = @id")
                .WithParameter("@id", ToDocumentId(commentId));
            var iterator = _container.GetItemQueryIterator<JObject>(query);
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                var doc = response.FirstOrDefault();
                if (doc != null)
                {
                    return ToBlogComment(doc);
                }
            }

            return null;
        }

        public async Task<BlogComment> UpsertAsync(BlogComment comment)
        {
            if (comment.Id == Guid.Empty)
            {
                comment.Id = Guid.NewGuid();
            }

            await _container.UpsertItemAsync(ToBlogCommentDocument(comment), CosmosPartitionKey.None);
            return comment;
        }

        public async Task DeleteAsync(Guid commentId)
        {
            try
            {
                await _container.DeleteItemAsync<JObject>(ToDocumentId(commentId), CosmosPartitionKey.None);
            }
            catch (Microsoft.Azure.Cosmos.CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // idempotent
            }
        }

        public async Task DeleteByBlogPostCascadeAsync(Guid blogPostId)
        {
            var comments = await GetByBlogPostIdAsync(blogPostId);
            foreach (var comment in comments)
            {
                await DeleteAsync(comment.Id);
            }
        }

        private static string ToDocumentId(Guid id) => $"BlogComment|{id:D}";

        private static BlogComment ToBlogComment(JObject doc)
        {
            var rawId = doc.Value<string>("id") ?? string.Empty;
            var parts = rawId.Split('|', StringSplitOptions.RemoveEmptyEntries);
            var parsedId = Guid.TryParse(parts.LastOrDefault(), out var id) ? id : Guid.Empty;
            var blogPostRaw = doc.Value<string>("BlogPostId");
            var parsedBlogPostId = Guid.TryParse(blogPostRaw, out var blogPostId) ? blogPostId : Guid.Empty;

            return new BlogComment
            {
                Id = parsedId,
                BlogPostId = parsedBlogPostId,
                AuthorUniqueId = doc.Value<string>("AuthorUniqueId"),
                AuthorDisplayName = doc.Value<string>("AuthorDisplayName"),
                Content = doc.Value<string>("Content"),
                CreatedUtc = doc["CreatedUtc"]?.ToObject<DateTime>() ?? DateTime.MinValue
            };
        }

        private static JObject ToBlogCommentDocument(BlogComment comment)
        {
            return new JObject
            {
                ["id"] = ToDocumentId(comment.Id),
                ["Discriminator"] = "BlogComment",
                ["BlogPostId"] = comment.BlogPostId.ToString("D"),
                ["AuthorUniqueId"] = comment.AuthorUniqueId,
                ["AuthorDisplayName"] = comment.AuthorDisplayName,
                ["Content"] = comment.Content ?? string.Empty,
                ["CreatedUtc"] = JToken.FromObject(comment.CreatedUtc)
            };
        }
    }
}
