using System;
using System.Collections.Generic;
using Web.Models;

namespace Web.PresentationModels
{
    public class VendorSummary
    {
        public Guid Id { get; set; }

        public string Name { get; set; }

        public List<string> Categories { get; set; }

        public bool IsNeighborAffiliated { get; set; }

        public string Phone { get; set; }

        public string Email { get; set; }

        public string Website { get; set; }

        public string Notes { get; set; }

        public int ReviewCount { get; set; }

        public DateTime? LastReviewModifiedUtc { get; set; }

        public static VendorSummary FromStorageModel(Vendor vendor, int reviewCount, DateTime? lastReviewModifiedUtc)
        {
            return new VendorSummary
            {
                Id = vendor.Id,
                Name = vendor.Name,
                Categories = vendor.Categories ?? new List<string>(),
                IsNeighborAffiliated = vendor.IsNeighborAffiliated,
                Phone = vendor.Phone,
                Email = vendor.Email,
                Website = vendor.Website,
                Notes = vendor.Notes,
                ReviewCount = reviewCount,
                LastReviewModifiedUtc = lastReviewModifiedUtc,
            };
        }
    }
}
