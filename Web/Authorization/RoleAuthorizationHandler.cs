using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using Web.Services.Repositories;

namespace Web.Authorization
{
    public class RoleAuthorizationHandler : AuthorizationHandler<RoleAuthorizationRequirement>
    {
        private readonly IUserRepository _userRepository;
        private readonly ILogger<RoleAuthorizationHandler> _logger;

        public RoleAuthorizationHandler(IUserRepository userRepository, ILogger<RoleAuthorizationHandler> logger)
        {
            _userRepository = userRepository;
            _logger = logger;
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
                _logger.LogWarning("Authorization failed for requirement {Role}: required claims are missing from the token.", requirement.RequiredRole);
                return;
            }

            var storedUser = await _userRepository.GetByUniqueIdAsync(uniqueId);
            if (storedUser == null)
            {
                _logger.LogWarning("Authorization failed for requirement {Role}: user {UniqueId} not found in database.", requirement.RequiredRole, uniqueId);
                return;
            }

            if (storedUser.Roles == null)
            {
                _logger.LogWarning("Authorization failed for requirement {Role}: user {UniqueId} has null roles.", requirement.RequiredRole, uniqueId);
                return;
            }

            if (storedUser.Roles.Contains(requirement.RequiredRole))
            {
                context.Succeed(requirement);
                return;
            }

            // Legacy accounts may have Administrator without Resident; Resident-gated endpoints should still allow them.
            if (requirement.RequiredRole == Models.User.Role.Resident &&
                storedUser.Roles.Contains(Models.User.Role.Administrator))
            {
                context.Succeed(requirement);
                return;
            }

            _logger.LogWarning("Authorization failed: user {UniqueId} does not have required role {Role}.", uniqueId, requirement.RequiredRole);
        }
    }
}
