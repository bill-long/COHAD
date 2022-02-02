using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
                // Includes are not supported, and we don't want this to be an owned type, so we're manually handling these references
                // See https://github.com/dotnet/efcore/issues/16920 for some of the issues with referenced types in Cosmos DB
                // See also https://docs.microsoft.com/en-us/ef/core/providers/cosmos/limitations
                var ownedHomes = user.OwnedHomeIds == null ? new List<Home>() : await _userRepository.Homes.Where(h => user.OwnedHomeIds.Contains(h.Id)).ToListAsync();
                return PresentationUser.FromStorageModel(user, ownedHomes);
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
                OwnedHomeIds = new List<System.Guid>(),
                UniqueId = uniqueId
            };

            await _userRepository.Users.AddAsync(newUser);
            await _userRepository.SaveChangesAsync();
            return PresentationUser.FromStorageModel(newUser, new List<Home>());
        }
    }
}
