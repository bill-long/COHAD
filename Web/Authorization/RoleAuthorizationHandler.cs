using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Web.Models;
using Web.Repository;

namespace Web.Authorization
{
    public class RoleAuthorizationHandler : AuthorizationHandler<RoleAuthorizationRequirement>
    {
        private readonly AzureTableRepository<User> _userRepository;

        public RoleAuthorizationHandler(AzureTableRepository<User> userRepository)
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

            var storedUser = await _userRepository.FindByKey(nameId);
            if (storedUser == null || storedUser.Role < requirement.MinimumRole)
            {
                return;
            }

            context.Succeed(requirement);
        }
    }
}
