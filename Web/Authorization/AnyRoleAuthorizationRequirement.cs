using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using Web.Models;
using Web.Services;

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
        private readonly ICurrentUserAccessor _currentUser;
        private readonly ILogger<AnyRoleAuthorizationHandler> _logger;

        public AnyRoleAuthorizationHandler(ICurrentUserAccessor currentUser, ILogger<AnyRoleAuthorizationHandler> logger)
        {
            _currentUser = currentUser;
            _logger = logger;
        }

        protected override async Task HandleRequirementAsync(
            AuthorizationHandlerContext context,
            AnyRoleAuthorizationRequirement requirement
        )
        {
            // Asked of the accessor rather than parsed here, so the id these warnings name is the one
            // that was actually looked up, and the failure modes stay apart - the read returns null for
            // both a malformed token and an unknown user.
            var uniqueId = _currentUser.TryGetUniqueId(context.User);
            if (uniqueId == null)
            {
                _logger.LogWarning("AnyRole authorization failed: required claims are missing from the token.");
                return;
            }

            // Read through the request-scoped accessor: the endpoint that runs next asks for the same
            // user, and this way both get one point read rather than two.
            var storedUser = await _currentUser.GetAsync(context.User);
            if (storedUser?.Roles == null)
            {
                _logger.LogWarning(
                    "AnyRole authorization failed: user {UniqueId} not found or has null roles.",
                    uniqueId
                );
                return;
            }

            if (requirement.RequiredRoles.Any(r => storedUser.Roles.Contains(r)))
            {
                context.Succeed(requirement);
            }
        }
    }
}
