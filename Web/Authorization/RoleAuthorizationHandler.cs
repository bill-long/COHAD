using System.Linq;
using System.Security.Claims;
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
            var nameId = context.User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(nameId))
            {
                return;
            }

            var storedUser = await _userRepository.Users.FindAsync(nameId);
            if (storedUser == null || !storedUser.Roles.Contains(requirement.RequiredRole))
            {
                return;
            }

            context.Succeed(requirement);
        }
    }
}
