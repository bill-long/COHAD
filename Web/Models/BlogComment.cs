using System;

namespace Web.Models
{
    public class BlogComment
    {
        public Guid Id { get; set; }

        public Guid BlogPostId { get; set; }

        public string AuthorUniqueId { get; set; }

        public string AuthorDisplayName { get; set; }

        /// <summary>
        /// Limited Markdown content (bold, italic, links).
        /// </summary>
        public string Content { get; set; }

        public DateTime CreatedUtc { get; set; }
    }
}
