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
    [Authorize(Policy = "Committee")]
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
        [Authorize(Policy = "Resident")]
        [HttpPut]
        public async Task<IActionResult> Update([FromBody] UpdatedHome updatedHome)
        {
            var apiUser =
                await _dbContext.Users.FindAsync(Models.User.GetUniqueIdFromClaims(User.Claims));

            if (apiUser.OwnedHomes.FirstOrDefault(h => h.Id == updatedHome.Id) == null)
            {
                // This user doesn't own this home. Check roles.
                if (!apiUser.Roles.Contains(Models.User.Role.Committee))
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

            storedHome.EmailAddress = updatedHome.EmailAddress;
            storedHome.PhoneNumber = updatedHome.PhoneNumber;
            storedHome.Residents = updatedHome.Residents;

            storedHome.AuditLog ??= new List<AuditLogEntry>();
            storedHome.AuditLog.Insert(0, new AuditLogEntry
            {
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
