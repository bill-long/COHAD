using System;
using Web.Models;

namespace Web.PresentationModels
{
    public class VendorFlagNotificationPresentation
    {
        public Guid FlagId { get; set; }

        public Guid VendorId { get; set; }

        public string VendorName { get; set; }

        public string AuthorDisplayName { get; set; }

        public string FlagNote { get; set; }

        public DateTime CreatedUtc { get; set; }

        public static VendorFlagNotificationPresentation FromStorageModel(VendorFlag flag, Vendor vendor)
        {
            return new VendorFlagNotificationPresentation
            {
                FlagId = flag.Id,
                VendorId = vendor.Id,
                VendorName = vendor.Name,
                AuthorDisplayName = flag.AuthorDisplayName,
                FlagNote = flag.FlagNote,
                CreatedUtc = flag.CreatedUtc
            };
        }
    }
}
