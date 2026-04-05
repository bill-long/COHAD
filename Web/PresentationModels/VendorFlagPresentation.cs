using System;
using Web.Models;

namespace Web.PresentationModels
{
    public class VendorFlagPresentation
    {
        public Guid Id { get; set; }

        public string FlagNote { get; set; }

        public string Status { get; set; }

        /// <summary>Populated for admin responses only; null for resident responses.</summary>
        public string AuthorDisplayName { get; set; }

        /// <summary>Populated for admin responses only; null for resident responses.</summary>
        public string AuthorUniqueId { get; set; }

        public DateTime CreatedUtc { get; set; }

        public static VendorFlagPresentation FromStorageModel(VendorFlag flag, bool includeAuthor)
        {
            return new VendorFlagPresentation
            {
                Id = flag.Id,
                FlagNote = flag.FlagNote,
                Status = flag.Status,
                AuthorDisplayName = includeAuthor ? flag.AuthorDisplayName : null,
                AuthorUniqueId = includeAuthor ? flag.AuthorUniqueId : null,
                CreatedUtc = flag.CreatedUtc,
            };
        }
    }
}
