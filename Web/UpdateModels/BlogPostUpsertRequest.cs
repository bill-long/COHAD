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

        public bool RemoveFeaturedImage { get; set; }

        public IFormFile FeaturedImage { get; set; }
    }
}
