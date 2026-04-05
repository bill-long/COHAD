using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Web.Models;
using Web.Services.Repositories;

namespace Web.MockData
{
    public sealed class MockBlogPostRepository : IBlogPostRepository
    {
        private readonly Dictionary<Guid, BlogPost> _posts = new();
        private readonly Dictionary<Guid, string> _etags = new();

        public MockBlogPostRepository()
        {
            var now = DateTime.UtcNow;

            var post1Id = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
            _posts[post1Id] = new BlogPost
            {
                Id = post1Id,
                PublicSlug = $"{now.Year}-spring-garden-tips",
                Title = "Spring Garden Tips",
                Content =
                    "## Get Your Garden Ready for Spring\n\nAs the weather warms up, it's time to start thinking about your garden. Here are some tips from the Garden Club to help you get started:\n\n- **Clean up beds** — Remove dead leaves and debris from flower beds.\n- **Test your soil** — Good soil is the foundation of a great garden.\n- **Plan your plantings** — Consider native plants that thrive in our climate.\n\nThe Garden Club meets every second Saturday at the clubhouse. New members are always welcome!",
                Excerpt =
                    "As the weather warms up, it's time to start thinking about your garden. Here are some tips from the Garden Club.",
                PublishUtc = now.AddDays(-3),
                AuthorDisplayName = "Mock Resident",
                CreatedByUniqueId = MockDataConstants.AdminUniqueId,
                ModifiedByUniqueId = MockDataConstants.AdminUniqueId,
                CreatedUtc = now.AddDays(-3),
                ModifiedUtc = now.AddDays(-3),
            };

            var post2Id = Guid.Parse("b2c3d4e5-f6a7-8901-bcde-f12345678901");
            _posts[post2Id] = new BlogPost
            {
                Id = post2Id,
                PublicSlug = $"{now.Year}-welcome-new-neighbors",
                Title = "Welcome to Our New Neighbors!",
                Content =
                    "We're thrilled to welcome several new families to Canyon Oaks this month! The Welcome Committee has been busy delivering welcome baskets and helping our new neighbors settle in.\n\n### Meet & Greet\n\nJoin us at the next HOA social to meet our newest community members. Refreshments will be provided by the Social Committee.\n\nIf you're new to the neighborhood and haven't received your welcome basket yet, please reach out to the Welcome Committee through the Residents area.",
                Excerpt =
                    "We're thrilled to welcome several new families to Canyon Oaks this month! Join us at the next social to meet them.",
                PublishUtc = now.AddDays(-7),
                AuthorDisplayName = "Mock Resident",
                CreatedByUniqueId = MockDataConstants.AdminUniqueId,
                ModifiedByUniqueId = MockDataConstants.AdminUniqueId,
                CreatedUtc = now.AddDays(-7),
                ModifiedUtc = now.AddDays(-7),
            };
        }

        private void EnsureEtag(Guid id)
        {
            if (!_etags.ContainsKey(id))
            {
                _etags[id] = Guid.NewGuid().ToString("N");
            }
        }

        public Task<List<BlogPost>> GetAllAsync()
        {
            lock (_posts)
            {
                return Task.FromResult(_posts.Values.Select(ClonePost).ToList());
            }
        }

        public Task<List<BlogPost>> GetSlugCandidatesAsync()
        {
            lock (_posts)
            {
                var list = _posts
                    .Values.Select(p => new BlogPost
                    {
                        Id = p.Id,
                        PublicSlug = p.PublicSlug,
                        PreviousSlugs = p.PreviousSlugs?.ToList() ?? new List<string>(),
                        Title = p.Title,
                        PublishUtc = p.PublishUtc,
                        Content = null,
                    })
                    .ToList();
                return Task.FromResult(list);
            }
        }

        public Task<List<BlogPost>> GetPublishedAsync(DateTime asOfUtc)
        {
            lock (_posts)
            {
                var list = _posts
                    .Values.Where(p => p.PublishUtc <= asOfUtc)
                    .OrderByDescending(p => p.PublishUtc)
                    .Select(ClonePost)
                    .ToList();
                return Task.FromResult(list);
            }
        }

        public Task<BlogPost> GetByIdAsync(Guid id)
        {
            lock (_posts)
            {
                return Task.FromResult(_posts.TryGetValue(id, out var found) ? ClonePost(found) : null);
            }
        }

        public Task<BlogPost> GetByRouteSegmentAsync(string segment)
        {
            lock (_posts)
            {
                if (string.IsNullOrWhiteSpace(segment))
                {
                    return Task.FromResult<BlogPost>(null);
                }

                var normalizedSegment = segment.Trim().ToLowerInvariant();

                var match = _posts.Values.FirstOrDefault(p =>
                {
                    var currentSlug = BlogUrlSlug.ResolveUrlSegment(p);
                    return !string.IsNullOrWhiteSpace(currentSlug)
                        && currentSlug.Trim().ToLowerInvariant() == normalizedSegment;
                });

                match ??= _posts.Values.FirstOrDefault(p =>
                    p.PreviousSlugs?.Any(s =>
                        !string.IsNullOrWhiteSpace(s) && s.Trim().ToLowerInvariant() == normalizedSegment
                    ) == true
                );

                return Task.FromResult(match == null ? null : ClonePost(match));
            }
        }

        public Task<BlogPostReadResult> ReadAsync(Guid id)
        {
            lock (_posts)
            {
                if (!_posts.TryGetValue(id, out var p))
                {
                    return Task.FromResult<BlogPostReadResult>(null);
                }

                EnsureEtag(id);
                return Task.FromResult(new BlogPostReadResult { Post = ClonePost(p), ETag = _etags[id] });
            }
        }

        public Task<BlogPost> UpsertAsync(BlogPost blogPost)
        {
            lock (_posts)
            {
                var copy = ClonePost(blogPost);
                if (copy.Id == Guid.Empty)
                {
                    copy.Id = Guid.NewGuid();
                }

                _posts[copy.Id] = copy;
                _etags[copy.Id] = Guid.NewGuid().ToString("N");
                return Task.FromResult(ClonePost(copy));
            }
        }

        public Task<BlogPost> ReplaceAsync(BlogPost blogPost, string ifMatchEtag)
        {
            lock (_posts)
            {
                var id = blogPost.Id;
                if (!_posts.ContainsKey(id))
                {
                    throw new CosmosException("Not found", HttpStatusCode.NotFound, 0, string.Empty, 0);
                }

                EnsureEtag(id);
                if (_etags[id] != ifMatchEtag)
                {
                    throw new CosmosException(
                        "Precondition failed",
                        HttpStatusCode.PreconditionFailed,
                        0,
                        string.Empty,
                        0
                    );
                }

                var copy = ClonePost(blogPost);
                _posts[id] = copy;
                _etags[id] = Guid.NewGuid().ToString("N");
                return Task.FromResult(ClonePost(copy));
            }
        }

        public Task DeleteAsync(Guid id)
        {
            lock (_posts)
            {
                _posts.Remove(id);
                _etags.Remove(id);
            }

            return Task.CompletedTask;
        }

        private static BlogPost ClonePost(BlogPost post)
        {
            if (post == null)
            {
                return null;
            }

            return new BlogPost
            {
                Id = post.Id,
                PublicSlug = post.PublicSlug,
                PreviousSlugs = post.PreviousSlugs?.ToList() ?? new List<string>(),
                Title = post.Title,
                Content = post.Content,
                Excerpt = post.Excerpt,
                FeaturedImageBlobPath = post.FeaturedImageBlobPath,
                FeaturedImageDisplayName = post.FeaturedImageDisplayName,
                FeaturedImageContentType = post.FeaturedImageContentType,
                FeaturedImageSizeBytes = post.FeaturedImageSizeBytes,
                PublishUtc = post.PublishUtc,
                AuthorDisplayName = post.AuthorDisplayName,
                CreatedByUniqueId = post.CreatedByUniqueId,
                ModifiedByUniqueId = post.ModifiedByUniqueId,
                CreatedUtc = post.CreatedUtc,
                ModifiedUtc = post.ModifiedUtc,
            };
        }
    }
}
