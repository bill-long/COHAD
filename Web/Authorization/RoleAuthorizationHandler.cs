using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using Web.Services;

namespace Web.Authorization
{
    public class RoleAuthorizationHandler : AuthorizationHandler<RoleAuthorizationRequirement>
    {
        private readonly ICurrentUserAccessor _currentUser;
        private readonly ILogger<RoleAuthorizationHandler> _logger;

        public RoleAuthorizationHandler(ICurrentUserAccessor currentUser, ILogger<RoleAuthorizationHandler> logger)
        {
            _currentUser = currentUser;
            _logger = logger;
        }

        protected override async Task HandleRequirementAsync(
            AuthorizationHandlerContext context,
            RoleAuthorizationRequirement requirement
        )
        {
            // Asked of the accessor rather than parsed here, so the id these warnings name is the one
            // that was actually looked up. They keep "malformed token" distinct from "no such user" -
            // the read returns null for both, and these logs are the only production signal.
            var uniqueId = _currentUser.TryGetUniqueId(context.User);
            if (uniqueId == null)
            {
                _logger.LogWarning(
                    "Authorization failed for requirement {Role}: required claims are missing from the token.",
                    requirement.RequiredRole
                );
                return;
            }

            // Read through the request-scoped accessor: the endpoint that runs next asks for the same
            // user, and this way both get one point read rather than two.
            var storedUser = await _currentUser.GetAsync(context.User);
            if (storedUser == null)
            {
                _logger.LogWarning(
                    "Authorization failed for requirement {Role}: user {UniqueId} not found in database.",
                    requirement.RequiredRole,
                    uniqueId
                );
                return;
            }

            if (storedUser.Roles == null)
            {
                _logger.LogWarning(
                    "Authorization failed for requirement {Role}: user {UniqueId} has null roles.",
                    requirement.RequiredRole,
                    uniqueId
                );
                return;
            }

            if (storedUser.Roles.Contains(requirement.RequiredRole))
            {
                context.Succeed(requirement);
                return;
            }

            // Legacy accounts may have Administrator without Resident; Resident-gated endpoints should still allow them.
            if (
                requirement.RequiredRole == Models.User.Role.Resident
                && storedUser.Roles.Contains(Models.User.Role.Administrator)
            )
            {
                context.Succeed(requirement);
                return;
            }

            _logger.LogWarning(
                "Authorization failed: user {UniqueId} does not have required role {Role}.",
                uniqueId,
                requirement.RequiredRole
            );
        }
    }
}
