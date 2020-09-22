using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Web.Models;
using Web.Repository;

namespace Web.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class MeController : ControllerBase
    {
        private readonly CohadWebDbContext _userRepository;

        public MeController(CohadWebDbContext userRepository)
        {
            _userRepository = userRepository;
        }

        [HttpGet]
        public async Task<UserViewModel> Get()
        {
            var uniqueId = Models.User.GetUniqueIdFromClaims(User.Claims);
            var user = await _userRepository.Users.FindAsync(uniqueId);
            if (user != null)
            {
                return UserViewModel.FromUser(user);
            }

            var newUser = new User
            {
                NameIdentifier = User.Claims.First(c => c.Type == ClaimTypes.NameIdentifier).Value,
                GivenName = User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.GivenName)?.Value,
                Surname = User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Surname)?.Value,
                IdentityProvider = User.Claims.First(c => c.Type == "http://schemas.microsoft.com/identity/claims/identityprovider").Value,
                Emails = User.Claims.FirstOrDefault(c => c.Type == "emails")?.Value,
                StreetAddress = User.Claims.FirstOrDefault(c => c.Type == "streetAddress")?.Value,
                Roles = new List<User.Role>(),
                PromotionState = Models.User.PromotionStates.None,
                UniqueId = uniqueId
            };

            _userRepository.Users.Add(newUser);
            await _userRepository.SaveChangesAsync();
            return UserViewModel.FromUser(newUser);
        }

        [HttpPut("request-access/{streetAddress}")]
        public async Task<UserViewModel> RequestAccess(string streetAddress)
        {
            var nameId = User.Claims.First(c => c.Type == ClaimTypes.NameIdentifier).Value;
            var user = await _userRepository.Users.FindAsync(nameId);
            if (user == null)
            {
                // The client should always be calling Get before the user requests promotion.
                // If that didn't happen, something is weird. Just ignore it.
                return null;
            }

            if (user.PromotionState == Models.User.PromotionStates.Requested ||
                user.PromotionState == Models.User.PromotionStates.Denied)
            {
                // Also, if promotion was already requested, or it was denied, ignore it.
                return UserViewModel.FromUser(user);
            }

            user.StreetAddress = streetAddress;
            user.PromotionState = Models.User.PromotionStates.Requested;
            await _userRepository.SaveChangesAsync();
            return UserViewModel.FromUser(user);
        }

        public class UserViewModel
        {
            public string GivenName { get; set; }
            public string Surname { get; set; }
            public string DisplayName => GivenName + " " + Surname;
            public string StreetAddress { get; set; }
            public string Email { get; set; }
            public string IdentityProvider { get; set; }
            public List<string> Roles { get; set; }
            public User.PromotionStates PromotionState { get; set; }

            public static UserViewModel FromUser(User user)
            {
                return new UserViewModel
                {
                    GivenName = user.GivenName,
                    Surname = user.Surname,
                    StreetAddress = user.StreetAddress,
                    Email = user.Emails,
                    IdentityProvider = user.IdentityProvider,
                    Roles = user.Roles.Select(r => r.ToString()).ToList(),
                    PromotionState = user.PromotionState
                };
            }
        }
    }
}
