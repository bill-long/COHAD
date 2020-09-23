using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Web.Models;
using Web.PresentationModels;
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
        public async Task<PresentationUser> Get()
        {
            var uniqueId = Models.User.GetUniqueIdFromClaims(User.Claims);
            var user = await _userRepository.Users.FindAsync(uniqueId);
            if (user != null)
            {
                return PresentationUser.FromStorageModel(user);
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
                UniqueId = uniqueId
            };

            await _userRepository.Users.AddAsync(newUser);
            await _userRepository.SaveChangesAsync();
            return PresentationUser.FromStorageModel(newUser);
        }
    }
}
