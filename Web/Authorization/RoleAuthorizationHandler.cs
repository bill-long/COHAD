using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Web.Services.Repositories;

namespace Web.Authorization
{
    public class RoleAuthorizationHandler : AuthorizationHandler<RoleAuthorizationRequirement>
    {
        private readonly IUserRepository _userRepository;

        public RoleAuthorizationHandler(IUserRepository userRepository)
        {
            _userRepository = userRepository;
        }

        protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, RoleAuthorizationRequirement requirement)
        {
            string uniqueId;
            try
            {
                uniqueId = Models.User.GetUniqueIdFromClaims(context.User.Claims);
            }
            catch (InvalidOperationException)
            {
                return;
            }

            var storedUser = await _userRepository.GetByUniqueIdAsync(uniqueId);
            if (storedUser == null || storedUser.Roles == null || !storedUser.Roles.Contains(requirement.RequiredRole))
            {
                return;
            }

            context.Succeed(requirement);
        }
    }
}
