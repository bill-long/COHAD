using System;

namespace Web.Models
{
    public class VendorReview
    {
        public Guid Id { get; set; }

        public Guid VendorId { get; set; }

        public string AuthorUniqueId { get; set; }

        public string AuthorDisplayName { get; set; }

        public string ReviewText { get; set; }

        public DateTime CreatedUtc { get; set; }

        public DateTime ModifiedUtc { get; set; }
    }
}
