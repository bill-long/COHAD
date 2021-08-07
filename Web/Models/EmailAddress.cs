using System;

namespace Web.Models
{
    public class EmailAddress
    {
        public string Address { get; set; }

        public bool VisibleInDirectory { get; set; }

        public bool GroupEmailOptedIn { get; set; }

        /// <summary>
        /// Whether Board emails should be sent to this address.
        /// </summary>
        public bool BoardEmailOptedIn { get; set; }

        /// <summary>
        /// Whether Welcome emails should be sent to this address.
        /// </summary>
        public bool WelcomeEmailOptedIn { get; set; }

        /// <summary>
        /// Whether Garden Club emails should be sent to this address.
        /// </summary>
        public bool GardenClubEmailOptedIn { get; set; }
    }
}
