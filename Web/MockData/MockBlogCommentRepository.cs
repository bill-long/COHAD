using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Web.Models;
using Web.Services.Repositories;

namespace Web.MockData
{
    public sealed class MockBlogCommentRepository : IBlogCommentRepository
    {
        private readonly Dictionary<Guid, BlogComment> _comments = new();

        public MockBlogCommentRepository()
        {
            var now = DateTime.UtcNow;
            var springGardenPostId = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

            var c1 = new BlogComment
            {
                Id = Guid.Parse("c0c0c0c0-0001-0001-0001-000000000001"),
                BlogPostId = springGardenPostId,
                AuthorUniqueId = MockDataConstants.AdminUniqueId,
                AuthorDisplayName = "Mock Resident",
                Content = "Great tips! I can't wait to get started on my **flower beds** this weekend.",
                CreatedUtc = now.AddDays(-2)
            };

            var c2 = new BlogComment
            {
                Id = Guid.Parse("c0c0c0c0-0001-0001-0001-000000000002"),
                BlogPostId = springGardenPostId,
                AuthorUniqueId = MockDataConstants.SecondaryUserUniqueId,
                AuthorDisplayName = "Taylor Resident",
                Content = "Does anyone know a good *local nursery* for native plants?",
                CreatedUtc = now.AddDays(-1)
            };

            _comments[c1.Id] = c1;
            _comments[c2.Id] = c2;
        }

        public Task<List<BlogComment>> GetByBlogPostIdAsync(Guid blogPostId)
        {
            lock (_comments)
            {
                var results = _comments.Values
                    .Where(c => c.BlogPostId == blogPostId)
                    .OrderBy(c => c.CreatedUtc)
                    .Select(Clone)
                    .ToList();
                return Task.FromResult(results);
            }
        }

        public Task<BlogComment> GetByIdAsync(Guid commentId)
        {
            lock (_comments)
            {
                if (_comments.TryGetValue(commentId, out var comment))
                {
                    return Task.FromResult(Clone(comment));
                }

                return Task.FromResult<BlogComment>(null);
            }
        }

        public Task<BlogComment> UpsertAsync(BlogComment comment)
        {
            lock (_comments)
            {
                var clone = Clone(comment);
                if (clone.Id == Guid.Empty)
                {
                    clone.Id = Guid.NewGuid();
                }

                _comments[clone.Id] = clone;
                return Task.FromResult(Clone(clone));
            }
        }

        public Task DeleteAsync(Guid commentId)
        {
            lock (_comments)
            {
                _comments.Remove(commentId);
            }

            return Task.CompletedTask;
        }

        public Task DeleteByBlogPostCascadeAsync(Guid blogPostId)
        {
            lock (_comments)
            {
                var toRemove = _comments.Values
                    .Where(c => c.BlogPostId == blogPostId)
                    .Select(c => c.Id)
                    .ToList();

                foreach (var id in toRemove)
                {
                    _comments.Remove(id);
                }
            }

            return Task.CompletedTask;
        }

        private static BlogComment Clone(BlogComment comment)
        {
            if (comment == null)
            {
                return null;
            }

            return new BlogComment
            {
                Id = comment.Id,
                BlogPostId = comment.BlogPostId,
                AuthorUniqueId = comment.AuthorUniqueId,
                AuthorDisplayName = comment.AuthorDisplayName,
                Content = comment.Content,
                CreatedUtc = comment.CreatedUtc
            };
        }
    }
}
