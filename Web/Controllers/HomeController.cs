using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Web.Models;
using Web.Repository;
using Web.UpdateModels;

namespace Web.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize(Policy = "Resident")]
    public class HomeController : ControllerBase
    {
        private readonly CohadWebDbContext _dbContext;

        public HomeController(CohadWebDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        /// <summary>
        /// Gets all homes that exist.
        /// </summary>
        [Authorize(Policy = "Administrator")]
        public async Task<IEnumerable<Home>> Get()
        {
            return await _dbContext.Homes.ToListAsync();
        }

        /// <summary>
        /// Updates the properties of a home. Committee role can
        /// do this for any home, but Resident role can only do this
        /// for their owned homes.
        /// </summary>
        /// <param name="updatedHome"></param>
        /// <returns></returns>
        [HttpPut]
        public async Task<IActionResult> Update([FromBody] UpdatedHome updatedHome)
        {
            var apiUser =
                await _dbContext.Users.FindAsync(Models.User.GetUniqueIdFromClaims(User.Claims));

            // Necessary because Cosmos doesn't support Include() yet.
            var allHomes = await _dbContext.Homes.ToListAsync();

            if (apiUser.OwnedHomes?.FirstOrDefault(h => h.Id == updatedHome.Id) == null)
            {
                // This user doesn't own this home. Check roles.
                if (!apiUser.Roles.Contains(Models.User.Role.Administrator))
                {
                    return Forbid();
                }
            }

            // User has permissions to update, so let's do it.
            var storedHome = await _dbContext.Homes.FirstOrDefaultAsync(h => h.Id == updatedHome.Id);

            if (storedHome == null)
            {
                return NotFound();
            }

            // Make sure emails are valid
            foreach (var resident in updatedHome.Residents)
            {
                if (resident.EmailAddresses != null && resident.EmailAddresses.Any())
                {
                    resident.EmailAddresses =
                        resident.EmailAddresses
                            .Where(e => !string.IsNullOrWhiteSpace(e.Address) && e.Address.Contains("@", StringComparison.OrdinalIgnoreCase))
                            .ToList();
                }
            }

            storedHome.EmailAddress = updatedHome.EmailAddress;
            storedHome.PhoneNumber = updatedHome.PhoneNumber;
            storedHome.Residents = updatedHome.Residents;

            await _dbContext.AuditLog.AddAsync(new NewAuditLogEntry
            {
                Id = Guid.NewGuid(),
                SubjectId = storedHome.Id.ToString(),
                SubjectName = $"{storedHome.StreetNumber} {storedHome.StreetName}",
                Action = "Updated home information.",
                Time = DateTime.UtcNow,
                UserDisplayName = $"{apiUser.GivenName ?? ""} {apiUser.Surname ?? ""}",
                UserId = apiUser.UniqueId
            });

            await _dbContext.SaveChangesAsync();

            return Ok();
        }
    }
}
