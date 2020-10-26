using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Web.Models;
using Web.PresentationModels;
using Web.Repository;
using Web.UpdateModels;

namespace Web.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize(Policy = "Committee")]
    public class UserController : ControllerBase
    {
        private readonly CohadWebDbContext _dbContext;

        public UserController(CohadWebDbContext dbContext) {
            _dbContext = dbContext;
        }

        public async Task<IEnumerable<PresentationUser>> Get()
        {
            var allUsers = await _dbContext.Users.ToListAsync();

            var allHomes = await _dbContext.Homes.ToListAsync();

            return allUsers.Select(PresentationUser.FromStorageModel);
        }

        /// <summary>
        /// Updates the basic properties of a user. Residents can do
        /// this to themselves. Otherwise, requires Committee role.
        /// </summary>
        /// <param name="updatedUser"></param>
        /// <returns></returns>
        [Authorize(Policy = "Resident")]
        [HttpPut]
        public async Task<IActionResult> UpdateUserProperties([FromBody] UpdatedUser updatedUser)
        {
            var apiUser =
                await _dbContext.Users.FirstOrDefaultAsync(u =>
                    u.UniqueId == Models.User.GetUniqueIdFromClaims(User.Claims));

            if (apiUser == null)
            {
                return NotFound();
            }

            if (updatedUser.UniqueId != apiUser.UniqueId)
            {
                // The authenticated user is trying to update some other user. Check roles.
                if (!apiUser.Roles.Contains(Models.User.Role.Committee))
                {
                    return Forbid();
                }
            }

            var storedUser = await _dbContext.Users.FirstOrDefaultAsync(u => u.UniqueId == updatedUser.UniqueId);

            if (storedUser == null)
            {
                return NotFound();
            }

            storedUser.GivenName = updatedUser.GivenName;
            storedUser.Surname = updatedUser.Surname;
            storedUser.StreetAddress = updatedUser.StreetAddress;

            await _dbContext.AuditLog.AddAsync(new NewAuditLogEntry
            {
                Id = Guid.NewGuid(),
                SubjectId = storedUser.UniqueId,
                SubjectName = storedUser.Emails,
                Action = "Updated user properties.",
                Time = DateTime.UtcNow,
                UserDisplayName = $"{apiUser.GivenName ?? ""} {apiUser.Surname ?? ""}",
                UserId = apiUser.UniqueId
            });

            await _dbContext.SaveChangesAsync();

            return Ok();
        }

        /// <summary>
        /// Makes the specified user an owner of the specified home.
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="homeId"></param>
        /// <returns></returns>
        [HttpPut("{userId}/homes/add/{homeId}")]
        public async Task<IActionResult> AddOwnedHome(string userId, Guid homeId)
        {
            var apiUser = await _dbContext.Users.FindAsync(Models.User.GetUniqueIdFromClaims(User.Claims));

            var user = await _dbContext.Users.FindAsync(userId);

            // We have to do this to resolve OwnedHomes because Include is not supported yet:
            // https://docs.microsoft.com/en-us/ef/core/providers/cosmos/limitations
            var allHomes = await _dbContext.Homes.ToListAsync();

            var home = await _dbContext.Homes.FindAsync(homeId);

            if (user == null || home == null)
            {
                return NotFound();
            }

            user.OwnedHomes ??= new List<Home>();

            if (user.OwnedHomes.FirstOrDefault(h => h.Id == home.Id) != null)
            {
                return Conflict("The specified user is already an owner of the specified home.");
            }

            user.OwnedHomes.Add(home);

            await _dbContext.AuditLog.AddAsync(new NewAuditLogEntry
            {
                Id = Guid.NewGuid(),
                SubjectId = user.UniqueId,
                SubjectName = user.Emails,
                Action = "Assigned a home to this user.",
                Time = DateTime.UtcNow,
                UserDisplayName = $"{apiUser.GivenName ?? ""} {apiUser.Surname ?? ""}",
                UserId = apiUser.UniqueId
            });

            await _dbContext.SaveChangesAsync();

            return Ok();
        }

        /// <summary>
        /// Removes the specified user as an owner of the specified home.
        /// </summary>
        /// <param name="userId"></param>
        /// <param name="homeId"></param>
        /// <returns></returns>
        [HttpPut("{userId}/homes/remove/{homeId}")]
        public async Task<IActionResult> RemoveOwnedHome(string userId, Guid homeId)
        {
            var apiUser = await _dbContext.Users.FindAsync(Models.User.GetUniqueIdFromClaims(User.Claims));

            var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.UniqueId == userId);

            // We have to do this to resolve OwnedHomes because Include is not supported yet:
            // https://docs.microsoft.com/en-us/ef/core/providers/cosmos/limitations
            var allHomes = await _dbContext.Homes.ToListAsync();

            if (user == null)
            {
                return NotFound();
            }

            user.OwnedHomes ??= new List<Home>();

            if (user.OwnedHomes.FirstOrDefault(h => h.Id == homeId) == null)
            {
                return Conflict("The specified user is not an owner of the specified home.");
            }

            user.OwnedHomes = user.OwnedHomes.Where(h => h.Id != homeId).ToList();

            await _dbContext.AuditLog.AddAsync(new NewAuditLogEntry
            {
                Id = Guid.NewGuid(),
                SubjectId = user.UniqueId,
                SubjectName = user.Emails,
                Action = "Removed a home from this user.",
                Time = DateTime.UtcNow,
                UserDisplayName = $"{apiUser.GivenName ?? ""} {apiUser.Surname ?? ""}",
                UserId = apiUser.UniqueId
            });

            await _dbContext.SaveChangesAsync();

            return Ok();
        }

        [Authorize(Policy = "Committee")]
        [HttpPut("{userId}/roles/add/{roleName}")]
        public async Task<IActionResult> AddUserRole(string userId, string roleName)
        {
            var apiUser = await _dbContext.Users.FindAsync(Models.User.GetUniqueIdFromClaims(User.Claims));

            var userToModify = await _dbContext.Users.FindAsync(userId);

            if (userToModify == null)
            {
                return NotFound();
            }

            if (!Enum.TryParse<Models.User.Role>(roleName, out var roleToAdd))
            {
                return BadRequest();
            }

            if (roleToAdd == Models.User.Role.Administrator || roleToAdd == Models.User.Role.Committee)
            {
                // Only administrators can do this
                if (!apiUser.Roles.Contains(Models.User.Role.Administrator))
                {
                    return Forbid();
                }
            }

            // Not sure why this is necessary, but it doesn't detect role changes otherwise
            _dbContext.Update(userToModify);

            userToModify.Roles.Add(roleToAdd);

            await _dbContext.AuditLog.AddAsync(new NewAuditLogEntry
            {
                Id = Guid.NewGuid(),
                SubjectId = userToModify.UniqueId,
                SubjectName = userToModify.Emails,
                Action = $"Added role {roleToAdd} to this user.",
                Time = DateTime.UtcNow,
                UserDisplayName = $"{apiUser.GivenName ?? ""} {apiUser.Surname ?? ""}",
                UserId = apiUser.UniqueId
            });

            var result = await _dbContext.SaveChangesAsync();

            return Ok();
        }

        [Authorize(Policy = "Committee")]
        [HttpPut("{userId}/roles/remove/{roleName}")]
        public async Task<IActionResult> RemoveUserRole(string userId, string roleName)
        {
            var apiUser = await _dbContext.Users.FindAsync(Models.User.GetUniqueIdFromClaims(User.Claims));

            var userToModify = await _dbContext.Users.FindAsync(userId);

            if (userToModify == null)
            {
                return NotFound();
            }

            if (!Enum.TryParse<Models.User.Role>(roleName, out var roleToRemove))
            {
                return BadRequest();
            }

            if (roleToRemove == Models.User.Role.Administrator || roleToRemove == Models.User.Role.Committee)
            {
                // Only administrators can do this
                if (!apiUser.Roles.Contains(Models.User.Role.Administrator))
                {
                    return Forbid();
                }
            }

            // Not sure why this is necessary, but it doesn't detect role changes otherwise
            _dbContext.Update(userToModify);

            userToModify.Roles.Remove(roleToRemove);

            await _dbContext.AuditLog.AddAsync(new NewAuditLogEntry
            {
                Id = Guid.NewGuid(),
                SubjectId = userToModify.UniqueId,
                SubjectName = userToModify.Emails,
                Action = $"Removed role {roleToRemove} from this user.",
                Time = DateTime.UtcNow,
                UserDisplayName = $"{apiUser.GivenName ?? ""} {apiUser.Surname ?? ""}",
                UserId = apiUser.UniqueId
            });

            await _dbContext.SaveChangesAsync();

            return Ok();
        }
    }
}
