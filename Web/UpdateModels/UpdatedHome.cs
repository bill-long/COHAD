using System;
using System.Collections.Generic;
using Web.Models;

namespace Web.UpdateModels
{
    /// <summary>
    /// Model used for binding update actions in the API. The notable
    /// difference is that StreetName and StreetNumber are omitted,
    /// because they must not be changed.
    /// </summary>
    public class UpdatedHome
    {
        public Guid Id { get; set; }

        public PhoneNumber PhoneNumber { get; set; }

        public EmailAddress EmailAddress { get; set; }

        public List<Resident> Residents { get; set; }
    }
}
