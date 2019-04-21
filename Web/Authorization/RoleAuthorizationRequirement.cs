using Microsoft.AspNetCore.Authorization;
using Web.Models;

namespace Web.Authorization
{
    public class RoleAuthorizationRequirement : IAuthorizationRequirement
    {
        public User.Roles MinimumRole { get; }

        public RoleAuthorizationRequirement(User.Roles minimumRole)
        {
            MinimumRole = minimumRole;
        }
    }
}
