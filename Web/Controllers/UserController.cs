using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Web.Models;
using Web.PresentationModels;
using Web.Services;
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
        private readonly IEventSignupConversionService _signupConversion;
        private readonly ILogger<UserController> _logger;

        public UserController(
            IUserRepository userRepository,
            IHomeRepository homeRepository,
            IAuditLogRepository auditLogRepository,
            IEventSignupConversionService signupConversion,
            ILogger<UserController> logger
        )
        {
            _userRepository = userRepository;
            _homeRepository = homeRepository;
            _auditLogRepository = auditLogRepository;
            _signupConversion = signupConversion;
            _logger = logger;
        }

        public async Task<IEnumerable<PresentationUser>> Get()
        {
            var allUsers = await _userRepository.GetAllAsync();
            var allHomes = await _homeRepository.GetAllAsync();
            return allUsers.Select(u =>
                PresentationUser.FromStorageModel(
                    u,
                    allHomes.Where(h => u.OwnedHomeIds != null && u.OwnedHomeIds.Contains(h.Id)).ToList()
                )
            );
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

            await _auditLogRepository.AddAsync(
                new NewAuditLogEntry
                {
                    Id = Guid.NewGuid(),
                    SubjectId = storedUser.UniqueId,
                    SubjectName = storedUser.Emails,
                    Action = "Updated user properties.",
                    Time = DateTime.UtcNow,
                    UserDisplayName = $"{apiUser.GivenName ?? ""} {apiUser.Surname ?? ""}",
                    UserId = apiUser.UniqueId,
                }
            );

            await _userRepository.UpsertAsync(storedUser);
            return Ok();
        }

        [HttpPut("{userId}/associations")]
        public async Task<IActionResult> UpdateUserAssociations(
            string userId,
            [FromBody] UpdatedUserAssociations updatedAssociations
        )
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

            var requestedRoleNames = updatedAssociations?.RoleNames ?? new List<string>();
            var requestedRoles = new List<Models.User.Role>();
            foreach (var roleName in requestedRoleNames.Distinct())
            {
                if (!Enum.TryParse<Models.User.Role>(roleName, out var parsedRole))
                {
                    return BadRequest($"Unknown role '{roleName}'.");
                }

                requestedRoles.Add(parsedRole);
            }

            var requestedHomeIds = (updatedAssociations?.OwnedHomeIds ?? new List<Guid>()).Distinct().ToList();
            var existingHomes = await _homeRepository.GetByIdsAsync(requestedHomeIds);
            if (existingHomes.Count != requestedHomeIds.Count)
            {
                return BadRequest("One or more specified homes do not exist.");
            }

            // Enforce role hierarchy: every Administrator also receives Resident.
            if (
                requestedRoles.Contains(Models.User.Role.Administrator)
                && !requestedRoles.Contains(Models.User.Role.Resident)
            )
            {
                requestedRoles.Add(Models.User.Role.Resident);
            }

            userToModify.Roles = requestedRoles;
            userToModify.OwnedHomeIds = requestedHomeIds;

            await _auditLogRepository.AddAsync(
                new NewAuditLogEntry
                {
                    Id = Guid.NewGuid(),
                    SubjectId = userToModify.UniqueId,
                    SubjectName = userToModify.Emails,
                    Action = "Updated user role and home associations.",
                    Time = DateTime.UtcNow,
                    UserDisplayName = $"{apiUser.GivenName ?? ""} {apiUser.Surname ?? ""}",
                    UserId = apiUser.UniqueId,
                }
            );

            await _userRepository.UpsertAsync(userToModify);

            // Convert any user-based event signups to home-based now that the user has a home.
            // Only auto-convert when exactly one home is assigned; multi-home users require
            // manual migration to avoid mis-associating signups with the wrong household.
            if (requestedHomeIds.Count == 1)
            {
                var primaryHome = existingHomes.First(h => h.Id == requestedHomeIds[0]);
                var homeAddress = $"{primaryHome.StreetNumber} {primaryHome.StreetName}".Trim();
                try
                {
                    await _signupConversion.ConvertUserSignupsToHomeAsync(
                        userToModify.UniqueId,
                        primaryHome.Id,
                        homeAddress
                    );
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Event signup conversion failed for user {UserId} after home assignment to {HomeId}",
                        userToModify.UniqueId, primaryHome.Id);
                    try
                    {
                        await _auditLogRepository.AddAsync(
                            new NewAuditLogEntry
                            {
                                Id = Guid.NewGuid(),
                                SubjectId = userToModify.UniqueId,
                                SubjectName = userToModify.Emails,
                                Action = "Event signup conversion failed after home assignment. Run migration endpoint to retry.",
                                Time = DateTime.UtcNow,
                                UserDisplayName = $"{apiUser.GivenName ?? ""} {apiUser.Surname ?? ""}".Trim(),
                                UserId = apiUser.UniqueId,
                            }
                        );
                    }
                    catch
                    {
                        // Best-effort: do not fail the request if audit logging also fails.
                    }
                }
            }

            return Ok();
        }

        /// <summary>
        /// One-time migration: converts all existing user-based event signups to home-based
        /// for users who now own a home. Safe to run multiple times (idempotent).
        /// </summary>
        [HttpPost("admin/migrate-event-signups")]
        public async Task<IActionResult> MigrateEventSignups()
        {
            var apiUser = await _userRepository.GetByUniqueIdAsync(Models.User.GetUniqueIdFromClaims(User.Claims));
            if (apiUser == null)
            {
                return NotFound();
            }

            var result = await _signupConversion.MigrateAllUserSignupsAsync(_userRepository, _homeRepository);

            await _auditLogRepository.AddAsync(
                new NewAuditLogEntry
                {
                    Id = Guid.NewGuid(),
                    SubjectId = "migrate-event-signups",
                    SubjectName = "Event signup migration",
                    Action = $"Migrated event signups: {result.SignupsConverted} converted, {result.SignupsRemoved} removed, {result.SignupsSkipped} skipped.",
                    Time = DateTime.UtcNow,
                    UserDisplayName = $"{apiUser.GivenName ?? ""} {apiUser.Surname ?? ""}".Trim(),
                    UserId = apiUser.UniqueId,
                }
            );

            return Ok(result);
        }
    }
}
