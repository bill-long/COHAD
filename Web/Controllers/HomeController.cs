using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Web.Models;
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
        private readonly IAuditLogRepository _auditLogRepository;

        public HomeController(
            IUserRepository userRepository,
            IHomeRepository homeRepository,
            IAuditLogRepository auditLogRepository)
        {
            _userRepository = userRepository;
            _homeRepository = homeRepository;
            _auditLogRepository = auditLogRepository;
        }

        /// <summary>
        /// Gets all homes that exist.
        /// </summary>
        [Authorize(Policy = "Administrator")]
        public async Task<IEnumerable<Home>> Get()
        {
            return await _homeRepository.GetAllAsync();
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
                await _userRepository.GetByUniqueIdAsync(Models.User.GetUniqueIdFromClaims(User.Claims));

            if (apiUser.OwnedHomeIds?.FirstOrDefault(homeId => homeId == updatedHome.Id) == null)
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

            foreach (var resident in updatedHome.Residents)
            {
                if (resident.ResidentType == Resident.Type.Child)
                {
                    // Ensure we don't store contact info for children
                    // The front end should not allow this, but we check here just in case
                    resident.EmailAddresses = new List<EmailAddress>();
                    resident.PhoneNumbers = new List<PhoneNumber>();
                }
                else if (resident.EmailAddresses != null && resident.EmailAddresses.Any())
                {
                    // Make sure email addresses are valid
                    resident.EmailAddresses =
                        resident.EmailAddresses
                            .Where(e => !string.IsNullOrWhiteSpace(e.Address) && e.Address.Contains("@", StringComparison.OrdinalIgnoreCase))
                            .ToList();
                }
            }

            storedHome.EmailAddress = updatedHome.EmailAddress;
            storedHome.PhoneNumber = updatedHome.PhoneNumber;
            storedHome.Residents = updatedHome.Residents.Where(r => !string.IsNullOrEmpty(r.GivenName)).ToList();

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
    }
}
