using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Web.Repository;

namespace Web.Authorization
{
    public class RoleAuthorizationHandler : AuthorizationHandler<RoleAuthorizationRequirement>
    {
        private readonly CohadWebDbContext _userRepository;

        public RoleAuthorizationHandler(CohadWebDbContext userRepository)
        {
            _userRepository = userRepository;
        }

        protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, RoleAuthorizationRequirement requirement)
        {
            var uniqueId = Models.User.GetUniqueIdFromClaims(context.User.Claims);
            if (string.IsNullOrEmpty(uniqueId))
            {
                return;
            }

            var storedUser = await _userRepository.Users.FindAsync(uniqueId);
            if (storedUser == null || !storedUser.Roles.Contains(requirement.RequiredRole))
            {
                return;
            }

            context.Succeed(requirement);
        }
    }
}
