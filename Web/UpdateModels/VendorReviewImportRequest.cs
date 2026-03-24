using System;

namespace Web.UpdateModels
{
    public class VendorReviewImportRequest
    {
        public Guid VendorId { get; set; }

        public string AuthorDisplayName { get; set; }

        public string ReviewText { get; set; }
    }
}
