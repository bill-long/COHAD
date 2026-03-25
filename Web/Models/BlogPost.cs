using System;

namespace Web.Models
{
    public class BlogPost
    {
        public Guid Id { get; set; }

        /// <summary>
        /// Public URL segment (e.g. <c>2026-spring-garden-tips</c>).
        /// </summary>
        public string PublicSlug { get; set; }

        public string Title { get; set; }

        /// <summary>
        /// Markdown body from the SPA editor; rendered to HTML for display.
        /// </summary>
        public string Content { get; set; }

        /// <summary>
        /// Short plain-text excerpt shown in cards and OG descriptions.
        /// Auto-generated from <see cref="Content"/> when not supplied explicitly.
        /// </summary>
        public string Excerpt { get; set; }

        public string FeaturedImageBlobPath { get; set; }

        public string FeaturedImageDisplayName { get; set; }

        public string FeaturedImageContentType { get; set; }

        public long? FeaturedImageSizeBytes { get; set; }

        public DateTime PublishUtc { get; set; }

        public string AuthorDisplayName { get; set; }

        public string CreatedByUniqueId { get; set; }

        public string ModifiedByUniqueId { get; set; }

        public DateTime CreatedUtc { get; set; }

        public DateTime ModifiedUtc { get; set; }
    }
}
