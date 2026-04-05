using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Web.Models;
using Web.Services;
using Web.Services.Repositories;
using Web.UpdateModels;

namespace Web.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize(Policy = "Resident")]
    public class HomeController : ControllerBase
    {
        private readonly IUserRepository _userRepository;
        private readonly IHomeRepository _homeRepository;
        private readonly IResidentRepository _residentRepository;
        private readonly IAuditLogRepository _auditLogRepository;
        private readonly ResidentCleanupService _residentCleanup;

        public HomeController(
            IUserRepository userRepository,
            IHomeRepository homeRepository,
            IResidentRepository residentRepository,
            IAuditLogRepository auditLogRepository,
            ResidentCleanupService residentCleanup)
        {
            _userRepository = userRepository;
            _homeRepository = homeRepository;
            _residentRepository = residentRepository;
            _auditLogRepository = auditLogRepository;
            _residentCleanup = residentCleanup;
        }

        /// <summary>
        /// Gets all homes that exist.
        /// </summary>
        [Authorize(Policy = "Administrator")]
        public async Task<IEnumerable<Home>> Get()
        {
            var homes = await _homeRepository.GetAllAsync();
            var users = await _userRepository.GetAllAsync();
            var allResidents = await _residentRepository.GetAllAsync();
            PopulateAssociatedUsers(homes, users);
            PopulateResidents(homes, allResidents);
            return homes;
        }

        /// <summary>
        /// Updates the properties of a home. Committee role can
        /// do this for any home, but Resident role can only do this
        /// for their owned homes.
        /// </summary>
        [HttpPut]
        public async Task<IActionResult> Update([FromBody] UpdatedHome updatedHome)
        {
            var apiUser =
                await _userRepository.GetByUniqueIdAsync(Models.User.GetUniqueIdFromClaims(User.Claims));

            var ownsHome = apiUser.OwnedHomeIds != null && apiUser.OwnedHomeIds.Contains(updatedHome.Id);
            if (!ownsHome)
            {
                // This user doesn't own this home. Check roles.
                if (!apiUser.Roles.Contains(Models.User.Role.Administrator))
                {
                    return Forbid();
                }
            }

            // User has permissions to update, so let's do it.
            var storedHome = await _homeRepository.GetByIdAsync(updatedHome.Id);

            if (storedHome == null)
            {
                return NotFound();
            }

            // Validate and sanitize incoming residents.
            var incomingResidents = (updatedHome.Residents ?? new List<Resident>())
                .Where(r => !string.IsNullOrEmpty(r.GivenName))
                .ToList();

            foreach (var resident in incomingResidents)
            {
                resident.HomeId = updatedHome.Id;

                if (resident.ResidentType == Resident.Type.Child)
                {
                    resident.EmailAddresses = new List<EmailAddress>();
                    resident.PhoneNumbers = new List<PhoneNumber>();
                }
                else if (resident.EmailAddresses != null && resident.EmailAddresses.Any())
                {
                    resident.EmailAddresses =
                        resident.EmailAddresses
                            .Where(e => !string.IsNullOrWhiteSpace(e.Address) && e.Address.Contains("@", StringComparison.OrdinalIgnoreCase))
                            .ToList();
                }
            }

            // Diff against existing residents and apply creates/updates/deletes.
            var existingResidents = await _residentRepository.GetByHomeIdAsync(updatedHome.Id);
            var existingById = existingResidents.Where(r => r.Id != Guid.Empty).ToDictionary(r => r.Id);

            foreach (var incoming in incomingResidents)
            {
                if (incoming.Id == Guid.Empty || !existingById.ContainsKey(incoming.Id))
                {
                    // New resident — assign ID.
                    if (incoming.Id == Guid.Empty)
                        incoming.Id = Guid.NewGuid();
                    await _residentRepository.UpsertAsync(incoming);
                }
                else
                {
                    // Existing resident — update.
                    await _residentRepository.UpsertAsync(incoming);
                    existingById.Remove(incoming.Id);
                }
            }

            // Any remaining in existingById were removed.
            var removedResidentIds = new List<Guid>();
            foreach (var removed in existingById.Values)
            {
                await _residentRepository.DeleteAsync(removed.Id);
                removedResidentIds.Add(removed.Id);
            }

            // Cascade: remove deleted residents from any committees they belong to.
            await _residentCleanup.RemoveFromCommitteesAsync(removedResidentIds);

            storedHome.EmailAddress = updatedHome.EmailAddress;
            storedHome.PhoneNumber = updatedHome.PhoneNumber;

            await _auditLogRepository.AddAsync(new NewAuditLogEntry
            {
                Id = Guid.NewGuid(),
                SubjectId = storedHome.Id.ToString(),
                SubjectName = $"{storedHome.StreetNumber} {storedHome.StreetName}",
                Action = "Updated home information.",
                Time = DateTime.UtcNow,
                UserDisplayName = $"{apiUser.GivenName ?? ""} {apiUser.Surname ?? ""}",
                UserId = apiUser.UniqueId
            });

            await _homeRepository.UpsertAsync(storedHome);

            return Ok();
        }

        [HttpDelete("{homeId}/owners/{userUniqueId}")]
        public async Task<IActionResult> RemoveAssociatedUser(Guid homeId, string userUniqueId)
        {
            var apiUser = await _userRepository.GetByUniqueIdAsync(Models.User.GetUniqueIdFromClaims(User.Claims));
            if (apiUser == null)
            {
                return NotFound();
            }

            var ownsHome = apiUser.OwnedHomeIds != null && apiUser.OwnedHomeIds.Contains(homeId);
            if (!ownsHome && !apiUser.Roles.Contains(Models.User.Role.Administrator))
            {
                return Forbid();
            }

            var userToUpdate = await _userRepository.GetByUniqueIdAsync(userUniqueId);
            if (userToUpdate == null)
            {
                return NotFound();
            }

            userToUpdate.OwnedHomeIds ??= new List<Guid>();
            if (!userToUpdate.OwnedHomeIds.Contains(homeId))
            {
                return Conflict("The specified user is not associated with the specified home.");
            }

            userToUpdate.OwnedHomeIds = userToUpdate.OwnedHomeIds.Where(h => h != homeId).ToList();
            await _auditLogRepository.AddAsync(new NewAuditLogEntry
            {
                Id = Guid.NewGuid(),
                SubjectId = userToUpdate.UniqueId,
                SubjectName = userToUpdate.Emails,
                Action = $"Removed home {homeId:D} from this user.",
                Time = DateTime.UtcNow,
                UserDisplayName = $"{apiUser.GivenName ?? ""} {apiUser.Surname ?? ""}",
                UserId = apiUser.UniqueId
            });

            await _userRepository.UpsertAsync(userToUpdate);
            return Ok();
        }

        private static void PopulateAssociatedUsers(List<Home> homes, List<User> users)
        {
            foreach (var home in homes)
            {
                home.AssociatedUsers = users
                    .Where(u => u.OwnedHomeIds != null && u.OwnedHomeIds.Contains(home.Id))
                    .Select(u => new HomeAssociatedUser
                    {
                        UniqueId = u.UniqueId,
                        GivenName = u.GivenName,
                        Surname = u.Surname,
                        Emails = u.Emails,
                        IdentityProvider = u.IdentityProvider
                    })
                    .ToList();
            }
        }

        private static void PopulateResidents(List<Home> homes, List<Resident> allResidents)
        {
            var byHomeId = allResidents.GroupBy(r => r.HomeId).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var home in homes)
            {
                home.Residents = byHomeId.TryGetValue(home.Id, out var residents) ? residents : new List<Resident>();
            }
        }
    }
}
