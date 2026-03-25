using System;

namespace Web.Models
{
    public class VendorFlag
    {
        public Guid Id { get; set; }

        public Guid VendorId { get; set; }

        public string AuthorUniqueId { get; set; }

        public string AuthorDisplayName { get; set; }

        public string FlagNote { get; set; }

        /// <summary>"Pending" or "Resolved"</summary>
        public string Status { get; set; }

        public DateTime CreatedUtc { get; set; }

        /// <summary>DateTime.MinValue when unresolved (same sentinel pattern as VendorReview.ModifiedUtc).</summary>
        public DateTime ResolvedUtc { get; set; }
    }
}
