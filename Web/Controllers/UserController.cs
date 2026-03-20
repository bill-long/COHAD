using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Web.Models;
using Web.PresentationModels;
using Web.Services.Repositories;
using Web.UpdateModels;

namespace Web.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize(Policy = "Administrator")]
    public class UserController : ControllerBase
    {
        private readonly IUserRepository _userRepository;
        private readonly IHomeRepository _homeRepository;
        private readonly IAuditLogRepository _auditLogRepository;

        public UserController(
            IUserRepository userRepository,
            IHomeRepository homeRepository,
            IAuditLogRepository auditLogRepository)
        {
            _userRepository = userRepository;
            _homeRepository = homeRepository;
            _auditLogRepository = auditLogRepository;
        }

        public async Task<IEnumerable<PresentationUser>> Get()
        {
            var allUsers = await _userRepository.GetAllAsync();
            var allHomes = await _homeRepository.GetAllAsync();
            return allUsers.Select(u => PresentationUser.FromStorageModel(u, allHomes.Where(h => u.OwnedHomeIds != null && u.OwnedHomeIds.Contains(h.Id)).ToList()));
        }

        [Authorize(Policy = "Resident")]
        [HttpPut]
        public async Task<IActionResult> UpdateUserProperties([FromBody] UpdatedUser updatedUser)
        {
            var apiUser = await _userRepository.GetByUniqueIdAsync(Models.User.GetUniqueIdFromClaims(User.Claims));
            if (apiUser == null)
            {
                return NotFound();
            }

            if (updatedUser.UniqueId != apiUser.UniqueId && !apiUser.Roles.Contains(Models.User.Role.Administrator))
            {
                return Forbid();
            }

            var storedUser = await _userRepository.GetByUniqueIdAsync(updatedUser.UniqueId);
            if (storedUser == null)
            {
                return NotFound();
            }

            storedUser.GivenName = updatedUser.GivenName;
            storedUser.Surname = updatedUser.Surname;
            storedUser.StreetAddress = updatedUser.StreetAddress;

            await _auditLogRepository.AddAsync(new NewAuditLogEntry
            {
                Id = Guid.NewGuid(),
                SubjectId = storedUser.UniqueId,
                SubjectName = storedUser.Emails,
                Action = "Updated user properties.",
                Time = DateTime.UtcNow,
                UserDisplayName = $"{apiUser.GivenName ?? ""} {apiUser.Surname ?? ""}",
                UserId = apiUser.UniqueId
            });

            await _userRepository.UpsertAsync(storedUser);
            return Ok();
        }

        [HttpPut("{userId}/homes/add/{homeId}")]
        public async Task<IActionResult> AddOwnedHome(string userId, Guid homeId)
        {
            var apiUser = await _userRepository.GetByUniqueIdAsync(Models.User.GetUniqueIdFromClaims(User.Claims));
            if (apiUser == null)
            {
                return NotFound();
            }

            var user = await _userRepository.GetByUniqueIdAsync(userId);
            var home = await _homeRepository.GetByIdAsync(homeId);
            if (user == null || home == null)
            {
                return NotFound();
            }

            user.OwnedHomeIds ??= new List<Guid>();
            if (user.OwnedHomeIds.Contains(home.Id))
            {
                return Conflict("The specified user is already an owner of the specified home.");
            }

            user.OwnedHomeIds.Add(home.Id);
            await _auditLogRepository.AddAsync(new NewAuditLogEntry
            {
                Id = Guid.NewGuid(),
                SubjectId = user.UniqueId,
                SubjectName = user.Emails,
                Action = "Assigned a home to this user.",
                Time = DateTime.UtcNow,
                UserDisplayName = $"{apiUser.GivenName ?? ""} {apiUser.Surname ?? ""}",
                UserId = apiUser.UniqueId
            });

            await _userRepository.UpsertAsync(user);
            return Ok();
        }

        [HttpPut("{userId}/homes/remove/{homeId}")]
        public async Task<IActionResult> RemoveOwnedHome(string userId, Guid homeId)
        {
            var apiUser = await _userRepository.GetByUniqueIdAsync(Models.User.GetUniqueIdFromClaims(User.Claims));
            if (apiUser == null)
            {
                return NotFound();
            }

            var user = await _userRepository.GetByUniqueIdAsync(userId);
            if (user == null)
            {
                return NotFound();
            }

            user.OwnedHomeIds ??= new List<Guid>();
            if (!user.OwnedHomeIds.Contains(homeId))
            {
                return Conflict("The specified user is not an owner of the specified home.");
            }

            user.OwnedHomeIds = user.OwnedHomeIds.Where(h => h != homeId).ToList();
            await _auditLogRepository.AddAsync(new NewAuditLogEntry
            {
                Id = Guid.NewGuid(),
                SubjectId = user.UniqueId,
                SubjectName = user.Emails,
                Action = "Removed a home from this user.",
                Time = DateTime.UtcNow,
                UserDisplayName = $"{apiUser.GivenName ?? ""} {apiUser.Surname ?? ""}",
                UserId = apiUser.UniqueId
            });

            await _userRepository.UpsertAsync(user);
            return Ok();
        }

        [HttpPut("{userId}/roles/add/{roleName}")]
        public async Task<IActionResult> AddUserRole(string userId, string roleName)
        {
            var apiUser = await _userRepository.GetByUniqueIdAsync(Models.User.GetUniqueIdFromClaims(User.Claims));
            if (apiUser == null)
            {
                return NotFound();
            }

            var userToModify = await _userRepository.GetByUniqueIdAsync(userId);
            if (userToModify == null)
            {
                return NotFound();
            }

            if (!Enum.TryParse<Models.User.Role>(roleName, out var roleToAdd))
            {
                return BadRequest();
            }

            userToModify.Roles ??= new List<Models.User.Role>();
            if (userToModify.Roles.Contains(roleToAdd))
            {
                return Ok();
            }

            userToModify.Roles.Add(roleToAdd);
            await _auditLogRepository.AddAsync(new NewAuditLogEntry
            {
                Id = Guid.NewGuid(),
                SubjectId = userToModify.UniqueId,
                SubjectName = userToModify.Emails,
                Action = $"Added role {roleToAdd} to this user.",
                Time = DateTime.UtcNow,
                UserDisplayName = $"{apiUser.GivenName ?? ""} {apiUser.Surname ?? ""}",
                UserId = apiUser.UniqueId
            });

            await _userRepository.UpsertAsync(userToModify);
            return Ok();
        }

        [HttpPut("{userId}/roles/remove/{roleName}")]
        public async Task<IActionResult> RemoveUserRole(string userId, string roleName)
        {
            var apiUser = await _userRepository.GetByUniqueIdAsync(Models.User.GetUniqueIdFromClaims(User.Claims));
            if (apiUser == null)
            {
                return NotFound();
            }

            var userToModify = await _userRepository.GetByUniqueIdAsync(userId);
            if (userToModify == null)
            {
                return NotFound();
            }

            if (!Enum.TryParse<Models.User.Role>(roleName, out var roleToRemove))
            {
                return BadRequest();
            }

            if (roleToRemove == Models.User.Role.Administrator)
            {
                return Forbid();
            }

            userToModify.Roles ??= new List<Models.User.Role>();
            userToModify.Roles.Remove(roleToRemove);
            await _auditLogRepository.AddAsync(new NewAuditLogEntry
            {
                Id = Guid.NewGuid(),
                SubjectId = userToModify.UniqueId,
                SubjectName = userToModify.Emails,
                Action = $"Removed role {roleToRemove} from this user.",
                Time = DateTime.UtcNow,
                UserDisplayName = $"{apiUser.GivenName ?? ""} {apiUser.Surname ?? ""}",
                UserId = apiUser.UniqueId
            });

            await _userRepository.UpsertAsync(userToModify);
            return Ok();
        }
    }
}
