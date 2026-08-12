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
        /// ETag for optimistic concurrency, populated on every repository read (Cosmos and Mock
        /// alike) and checked on write. Null only on instances that did not come from a repository
        /// read (request payloads, newly constructed homes), whose writes are blind.
        /// </summary>
        public string ETag { get; set; }
    }
}
