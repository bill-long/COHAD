using System;
using System.Collections.Generic;
using System.Linq;
using Web.Models;

namespace Web.PresentationModels
{
    public class PresentationUser
    {
        public string UniqueId { get; private set; }
        public string GivenName { get; private set; }
        public string Surname { get; private set; }
        public string DisplayName => GivenName + " " + Surname;
        public string StreetAddress { get; private set; }
        public string Email { get; private set; }
        public string IdentityProvider { get; private set; }
        public List<string> Roles { get; private set; }
        public List<Home> OwnedHomes { get; private set; }

        public static PresentationUser FromStorageModel(Models.User user, List<Models.Home> ownedHomes)
        {
            return new PresentationUser
            {
                UniqueId = user.UniqueId,
                GivenName = user.GivenName,
                Surname = user.Surname,
                StreetAddress = user.StreetAddress,
                Email = user.Emails,
                IdentityProvider = user.IdentityProvider,
                Roles = user.Roles.Select(r => r.ToString()).ToList(),
                OwnedHomes = ownedHomes
            };
        }
    }
}
