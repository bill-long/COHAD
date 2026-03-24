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

        /// <summary>All pending flags on this vendor. Non-null (even if empty) for admins; null for residents.</summary>
        public List<VendorFlagPresentation> PendingFlags { get; set; }

        /// <summary>The current resident's most recent flag on this vendor (any status), or null if none. Always null for admins.</summary>
        public VendorFlagPresentation MyFlag { get; set; }

        public static VendorDetail FromStorageModel(Vendor vendor, List<VendorReview> reviews, List<VendorFlag> flags, string currentUserUniqueId, bool isAdmin)
        {
            var safeReviews = reviews ?? new List<VendorReview>();
            var safeFlags = flags ?? new List<VendorFlag>();
            var reviewCount = safeReviews.Count;
            var latestReviewModifiedUtc = safeReviews.Count > 0
                ? safeReviews.Max(r => r.ModifiedUtc)
                : (System.DateTime?)null;
            var summary = FromStorageModel(vendor, reviewCount, latestReviewModifiedUtc);

            List<VendorFlagPresentation> pendingFlags = null;
            VendorFlagPresentation myFlag = null;

            if (isAdmin)
            {
                pendingFlags = safeFlags
                    .Where(f => f.Status == "Pending")
                    .OrderBy(f => f.CreatedUtc)
                    .Select(f => VendorFlagPresentation.FromStorageModel(f, includeAuthor: true))
                    .ToList();
            }
            else
            {
                var ownFlag = safeFlags
                    .Where(f => !string.IsNullOrWhiteSpace(currentUserUniqueId) &&
                                string.Equals(f.AuthorUniqueId, currentUserUniqueId, System.StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => f.CreatedUtc)
                    .FirstOrDefault();
                if (ownFlag != null)
                {
                    myFlag = VendorFlagPresentation.FromStorageModel(ownFlag, includeAuthor: false);
                }
            }

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
                LastReviewModifiedUtc = summary.LastReviewModifiedUtc,
                Address = vendor.Address,
                Notes = vendor.Notes,
                Reviews = safeReviews
                    .OrderByDescending(r => r.ModifiedUtc)
                    .Select(r => VendorReviewPresentation.FromStorageModel(
                        r,
                        isAdmin || (!string.IsNullOrWhiteSpace(currentUserUniqueId) &&
                                    string.Equals(r.AuthorUniqueId, currentUserUniqueId, System.StringComparison.OrdinalIgnoreCase))))
                    .ToList(),
                PendingFlags = pendingFlags,
                MyFlag = myFlag
            };
        }
    }
}
