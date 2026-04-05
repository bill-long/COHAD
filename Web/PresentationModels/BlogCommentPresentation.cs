using System;
using Web.Models;

namespace Web.PresentationModels
{
    public class BlogCommentPresentation
    {
        public Guid Id { get; set; }

        public string AuthorDisplayName { get; set; }

        public string Content { get; set; }

        public DateTime CreatedUtc { get; set; }

        public bool CanDelete { get; set; }

        public static BlogCommentPresentation FromStorageModel(BlogComment comment, bool canDelete)
        {
            return new BlogCommentPresentation
            {
                Id = comment.Id,
                AuthorDisplayName = comment.AuthorDisplayName,
                Content = comment.Content,
                CreatedUtc = comment.CreatedUtc,
                CanDelete = canDelete,
            };
        }
    }
}
