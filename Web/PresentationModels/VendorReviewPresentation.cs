using System;
using Web.Models;

namespace Web.PresentationModels
{
    public class VendorReviewPresentation
    {
        public Guid Id { get; set; }

        public string ReviewText { get; set; }

        public string AuthorDisplayName { get; set; }

        public DateTime CreatedUtc { get; set; }

        public DateTime ModifiedUtc { get; set; }

        /// <summary>True when <see cref="ModifiedUtc"/> is the unknown/import sentinel (not a real edit time).</summary>
        public bool ModifiedUtcIsUnknown { get; set; }

        public bool CanEdit { get; set; }

        public static VendorReviewPresentation FromStorageModel(VendorReview review, bool canEdit = false)
        {
            if (review == null)
            {
                return null;
            }

            return new VendorReviewPresentation
            {
                Id = review.Id,
                ReviewText = review.ReviewText,
                AuthorDisplayName = review.AuthorDisplayName,
                CreatedUtc = review.CreatedUtc,
                ModifiedUtc = review.ModifiedUtc,
                ModifiedUtcIsUnknown = VendorReviewTimestamps.IsUnknown(review.ModifiedUtc),
                CanEdit = canEdit
            };
        }
    }
}
