using System;
using Web.Models;

namespace Web.PresentationModels
{
    public class BlogPostCard
    {
        public Guid Id { get; protected set; }

        public string PublicSlug { get; protected set; }

        public string Title { get; protected set; }

        public string Excerpt { get; protected set; }

        public DateTime PublishUtc { get; protected set; }

        public string AuthorDisplayName { get; protected set; }

        /// <summary>
        /// The committee role name when the post was published under a committee identity,
        /// or <c>null</c> when published under the author's personal name.
        /// </summary>
        public string AuthorAsCommittee { get; protected set; }

        public bool HasFeaturedImage { get; protected set; }

        public string FeaturedImageContentType { get; protected set; }

        public string FeaturedImageDownloadUrl { get; protected set; }

        public static BlogPostCard FromStorageModel(BlogPost post)
        {
            var urlSegment = BlogUrlSlug.ResolveUrlSegment(post);
            return new BlogPostCard
            {
                Id = post.Id,
                PublicSlug = urlSegment,
                Title = post.Title,
                Excerpt = post.Excerpt,
                PublishUtc = post.PublishUtc,
                AuthorDisplayName = post.AuthorDisplayName,
                AuthorAsCommittee = post.AuthorAsCommittee?.ToString(),
                HasFeaturedImage = !string.IsNullOrWhiteSpace(post.FeaturedImageBlobPath),
                FeaturedImageContentType = post.FeaturedImageContentType,
                FeaturedImageDownloadUrl = !string.IsNullOrWhiteSpace(post.FeaturedImageBlobPath)
                    ? $"/api/blog/{Uri.EscapeDataString(urlSegment)}/image"
                    : null,
            };
        }
    }
}
