using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using Web.Models;
using Web.Services.Repositories;

namespace Web.Authorization
{
    /// <summary>
    /// Requires the user to have at least one of the specified roles.
    /// Used for endpoints that multiple committee roles can access (e.g. email job management).
    /// </summary>
    public class AnyRoleAuthorizationRequirement : IAuthorizationRequirement
    {
        public IReadOnlyList<User.Role> RequiredRoles { get; }

        public AnyRoleAuthorizationRequirement(params User.Role[] requiredRoles)
        {
            RequiredRoles = requiredRoles;
        }
    }

    public class AnyRoleAuthorizationHandler : AuthorizationHandler<AnyRoleAuthorizationRequirement>
    {
        private readonly IUserRepository _userRepository;
        private readonly ILogger<AnyRoleAuthorizationHandler> _logger;

        public AnyRoleAuthorizationHandler(IUserRepository userRepository, ILogger<AnyRoleAuthorizationHandler> logger)
        {
            _userRepository = userRepository;
            _logger = logger;
        }

        protected override async Task HandleRequirementAsync(
            AuthorizationHandlerContext context, AnyRoleAuthorizationRequirement requirement)
        {
            string uniqueId;
            try
            {
                uniqueId = User.GetUniqueIdFromClaims(context.User.Claims);
            }
            catch (System.InvalidOperationException)
            {
                _logger.LogWarning("AnyRole authorization failed: required claims are missing from the token.");
                return;
            }

            var storedUser = await _userRepository.GetByUniqueIdAsync(uniqueId);
            if (storedUser?.Roles == null)
            {
                _logger.LogWarning("AnyRole authorization failed: user {UniqueId} not found or has null roles.", uniqueId);
                return;
            }

            if (requirement.RequiredRoles.Any(r => storedUser.Roles.Contains(r)))
            {
                context.Succeed(requirement);
            }
        }
    }
}
