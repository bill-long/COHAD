using System;
using System.Collections.Generic;

namespace Web.Models
{
    public class HomeAssociatedUser
    {
        public string UniqueId { get; set; }

        public string GivenName { get; set; }

        public string Surname { get; set; }

        public string Emails { get; set; }

        public string IdentityProvider { get; set; }
    }

    public class Home
    {
        public Guid Id { get; set; }

        public int StreetNumber { get; set; }

        public string StreetName { get; set; }

        public PhoneNumber PhoneNumber { get; set; }

        public EmailAddress EmailAddress { get; set; }

        public List<Resident> Residents { get; set; }

        public List<HomeAssociatedUser> AssociatedUsers { get; set; }

        /// <summary>
        /// Cosmos DB ETag for optimistic concurrency. Populated on read, checked on write.
        /// Null when not loaded from Cosmos (new documents or mock data without versioning).
        /// </summary>
        public string ETag { get; set; }
    }
}
