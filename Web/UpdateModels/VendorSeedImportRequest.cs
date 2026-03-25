using System.Collections.Generic;

namespace Web.UpdateModels
{
    public class VendorSeedImportRequest
    {
        public List<VendorSeedImportVendor> Vendors { get; set; } = new();

        public List<VendorSeedImportReview> Reviews { get; set; } = new();

        public List<VendorSeedImportYouthService> YouthServices { get; set; } = new();
    }

    public class VendorSeedImportVendor
    {
        public string Fingerprint { get; set; }

        public string Name { get; set; }

        public List<string> Categories { get; set; } = new();

        public bool IsNeighborAffiliated { get; set; }

        public string Phone { get; set; }

        public string Email { get; set; }

        public string Website { get; set; }

        public string Address { get; set; }

        public string Notes { get; set; }

        /// <summary>Optional review text saved with the new vendor (same semantics as vendor create API).</summary>
        public string InitialReviewText { get; set; }
    }

    public class VendorSeedImportReview
    {
        public string VendorFingerprint { get; set; }

        public string AuthorDisplayName { get; set; }

        public string ReviewText { get; set; }
    }

    public class VendorSeedImportYouthService
    {
        public string Name { get; set; }

        public List<string> Services { get; set; } = new();

        public int? BornYear { get; set; }

        public string Phone { get; set; }

        public int ContactMethod { get; set; } = 1;

        public string Email { get; set; }

        public string Address { get; set; }

        public string ParentNote { get; set; }
    }
}
