using Web.Models;

namespace Web.PresentationModels
{
    public class BlogPostDetail : BlogPostCard
    {
        public string Content { get; private set; }

        public string FeaturedImageDisplayName { get; private set; }

        public static new BlogPostDetail FromStorageModel(BlogPost post)
        {
            var card = BlogPostCard.FromStorageModel(post);
            return new BlogPostDetail
            {
                Id = card.Id,
                PublicSlug = card.PublicSlug,
                Title = card.Title,
                Excerpt = card.Excerpt,
                PublishUtc = card.PublishUtc,
                AuthorDisplayName = card.AuthorDisplayName,
                HasFeaturedImage = card.HasFeaturedImage,
                FeaturedImageContentType = card.FeaturedImageContentType,
                FeaturedImageDownloadUrl = card.FeaturedImageDownloadUrl,
                Content = post.Content,
                FeaturedImageDisplayName = post.FeaturedImageDisplayName
            };
        }
    }
}
