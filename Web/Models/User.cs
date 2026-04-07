using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;

namespace Web.Models
{
    /// <summary>
    /// User
    /// </summary>
    public class User
    {
        public string NameIdentifier { get; set; }

        public string GivenName { get; set; }

        public string Surname { get; set; }

        public string IdentityProvider { get; set; }

        public string Emails { get; set; }

        public string StreetAddress { get; set; }

        public List<Role> Roles { get; set; }

        public string UniqueId { get; set; }

        public List<Guid> OwnedHomeIds { get; set; }

        public DateTime? LastLoggedIn { get; set; }

        /// <summary>
        /// When the user last had no homes in <see cref="OwnedHomeIds"/> (UTC). Null while they own at least one home.
        /// </summary>
        public DateTime? UnassociatedSinceUtc { get; set; }

        /// <summary>
        /// When the user last had no roles (UTC). Null while they have one or more roles.
        /// </summary>
        public DateTime? NoRolesSinceUtc { get; set; }

        /// <summary>
        /// Whether this user satisfies Resident-level access. True when the user has the Resident role
        /// or the Administrator role (which implies Resident access per the role hierarchy).
        /// </summary>
        public bool HasResidentAccess =>
            Roles != null && (Roles.Contains(Role.Resident) || Roles.Contains(Role.Administrator));

        public enum Role
        {
            Resident,
            Administrator,
            WelcomeCommittee,
            GardenClub,
            Board,
            SocialCommittee,
            SunshineCommittee,
            ArchitecturalCommittee,
        }

        public static string GetUniqueIdFromClaims(IEnumerable<Claim> claims)
        {
            var claimsList = claims.ToList();
            var nameId = claimsList.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value;
            if (nameId == null)
            {
                throw new InvalidOperationException(
                    "Cannot build a unique user ID: the NameIdentifier claim is missing."
                );
            }

            var idProvider =
                claimsList
                    .FirstOrDefault(c => c.Type == "http://schemas.microsoft.com/identity/claims/identityprovider")
                    ?.Value
                ?? "local";

            return idProvider + nameId;
        }
    }
}
