using System;
using System.Collections.Generic;
using Web.Models;

namespace Web.PresentationModels
{
    public class YouthServiceListingPresentation
    {
        public Guid Id { get; set; }

        public string Name { get; set; }

        public List<string> Services { get; set; }

        public int? BornYear { get; set; }

        public string Phone { get; set; }

        public string ContactMethod { get; set; }

        public string Email { get; set; }

        public string Address { get; set; }

        public string ParentNote { get; set; }

        public string OwnerUniqueId { get; set; }

        public bool CanEdit { get; set; }

        public static YouthServiceListingPresentation FromStorageModel(
            YouthServiceListing listing,
            bool canEdit = false
        )
        {
            return new YouthServiceListingPresentation
            {
                Id = listing.Id,
                Name = listing.Name,
                Services = listing.Services ?? new List<string>(),
                BornYear = listing.BornYear,
                Phone = listing.Phone,
                ContactMethod = listing.ContactMethod.ToString(),
                Email = listing.Email,
                Address = listing.Address,
                ParentNote = listing.ParentNote,
                OwnerUniqueId = listing.CreatedByUniqueId,
                CanEdit = canEdit,
            };
        }
    }
}
