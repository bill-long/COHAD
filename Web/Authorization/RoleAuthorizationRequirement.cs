using Microsoft.AspNetCore.Authorization;
using Web.Models;

namespace Web.Authorization
{
    public class RoleAuthorizationRequirement : IAuthorizationRequirement
    {
        public User.Role RequiredRole { get; }

        public RoleAuthorizationRequirement(User.Role requiredRole)
        {
            RequiredRole = requiredRole;
        }
    }
}
