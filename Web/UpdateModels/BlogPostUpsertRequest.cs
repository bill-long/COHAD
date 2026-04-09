using System;
using Microsoft.AspNetCore.Http;

namespace Web.UpdateModels
{
    public class BlogPostUpsertRequest
    {
        public Guid? Id { get; set; }

        public string Title { get; set; }

        public string Content { get; set; }

        public string Excerpt { get; set; }

        public DateTime? PublishUtc { get; set; }

        /// <summary>
        /// Optional committee role name (e.g. "Board") to publish the post under
        /// instead of the author's personal name. Null or empty means personal name.
        /// </summary>
        public string AuthorAsCommittee { get; set; }

        public bool RemoveFeaturedImage { get; set; }

        public IFormFile FeaturedImage { get; set; }
    }
}
