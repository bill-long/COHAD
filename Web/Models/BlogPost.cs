using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Web.Models
{
    public class BlogPost
    {
        /// <summary>Cosmos DB requires the JSON property <c>id</c> (lowercase).</summary>
        [JsonProperty("id")]
        public Guid Id { get; set; }

        /// <summary>
        /// Public URL segment (e.g. <c>2026-spring-garden-tips</c>).
        /// </summary>
        public string PublicSlug { get; set; }

        /// <summary>
        /// Previous slug values that should still resolve to this post (populated when a title or date change causes the slug to change).
        /// </summary>
        public List<string> PreviousSlugs { get; set; } = new List<string>();

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
