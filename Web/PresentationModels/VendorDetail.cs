using System.Collections.Generic;
using System.Linq;
using Web.Models;

namespace Web.PresentationModels
{
    public class VendorDetail : VendorSummary
    {
        public string Address { get; set; }

        public string Notes { get; set; }

        public List<VendorReviewPresentation> Reviews { get; set; }

        public static VendorDetail FromStorageModel(Vendor vendor, List<VendorReview> reviews, string currentUserUniqueId, bool isAdmin)
        {
            var safeReviews = reviews ?? new List<VendorReview>();
            var reviewCount = safeReviews.Count;
            var summary = FromStorageModel(vendor, reviewCount);
            return new VendorDetail
            {
                Id = summary.Id,
                Name = summary.Name,
                Categories = summary.Categories,
                IsNeighborAffiliated = summary.IsNeighborAffiliated,
                Phone = summary.Phone,
                Email = summary.Email,
                Website = summary.Website,
                ReviewCount = summary.ReviewCount,
                Address = vendor.Address,
                Notes = vendor.Notes,
                Reviews = safeReviews
                    .OrderByDescending(r => r.ModifiedUtc)
                    .Select(r => VendorReviewPresentation.FromStorageModel(
                        r,
                        isAdmin || (!string.IsNullOrWhiteSpace(currentUserUniqueId) &&
                                    string.Equals(r.AuthorUniqueId, currentUserUniqueId, System.StringComparison.OrdinalIgnoreCase))))
                    .ToList()
            };
        }
    }
}
