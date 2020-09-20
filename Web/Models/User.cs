using System;
using System.Collections.Generic;

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

        public PromotionStates PromotionState { get; set; }

        public enum Role
        {
            Resident,
            Committee,
            Administrator
        }

        /// <summary>
        /// After a user logs in for the first time, they will have a role of
        /// None. They can request promotion in order to ask an Admin to grant
        /// them the Member role, and Admins will be notified of this request.
        /// However, we don't want to let a user repeatedly request promotion,
        /// if they have already been denied.
        /// </summary>
        public enum PromotionStates
        {
            None, // Either the user has not requested, or they were approved and the state was cleared.
            Requested, // Currently awaiting approval.
            Denied // The user was denied and is now blocked from requesting promotion.
        }
    }
}
