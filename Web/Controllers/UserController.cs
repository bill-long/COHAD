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
        private readonly ICurrentUserAccessor _currentUser;
        private readonly IHomeRepository _homeRepository;
        private readonly IResidentRepository _residentRepository;
        private readonly IAuditLogRepository _auditLogRepository;
        private readonly IEventSignupConversionService _signupConversion;
        private readonly ILogger<UserController> _logger;

        public UserController(
            IUserRepository userRepository,
            ICurrentUserAccessor currentUser,
            IHomeRepository homeRepository,
            IResidentRepository residentRepository,
            IAuditLogRepository auditLogRepository,
            IEventSignupConversionService signupConversion,
            ILogger<UserController> logger
        )
        {
            _userRepository = userRepository;
            _currentUser = currentUser;
            _homeRepository = homeRepository;
            _residentRepository = residentRepository;
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
            var apiUser = await _currentUser.GetAsync(User);
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

            // Write then audit, best-effort, like the sibling association endpoints: the audit log
            // only ever describes changes that really happened, and a failed audit after an applied
            // write is logged rather than misreported as a failed save.
            // A lost write race surfaces as ConcurrencyConflictException, which the global
            // ConcurrencyConflictExceptionFilter maps to the 409 refresh guidance.
            await _userRepository.UpsertAsync(storedUser);

            try
            {
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
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(
                    ex,
                    "Failed to write the audit entry for an applied property update of user {UserId}",
                    storedUser.UniqueId
                );
            }

            return Ok();
        }

        [HttpPut("{userId}/associations")]
        public async Task<IActionResult> UpdateUserAssociations(
            string userId,
            [FromBody] UpdatedUserAssociations updatedAssociations
        )
        {
            var apiUser = await _currentUser.GetAsync(User);
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
            var homesTask = _homeRepository.GetByIdsAsync(requestedHomeIds);

            // Resident-link wire protocol: null (or an omitted property) leaves the stored link
            // unchanged - so a client that predates the link cannot wipe it by omission - while
            // Guid.Empty clears it explicitly and any other value sets it.
            var requestedResidentId = updatedAssociations?.ResidentId;
            var explicitLinkChange = requestedResidentId != null;
            var effectiveResidentId = explicitLinkChange
                ? (requestedResidentId!.Value == Guid.Empty ? (Guid?)null : requestedResidentId)
                : userToModify.ResidentId;

            // The link is validated against the request's home ids, so the resident read is
            // independent of the home read; start both so the two lookups run concurrently.
            var residentTask =
                effectiveResidentId != null
                    ? _residentRepository.GetByIdAsync(effectiveResidentId.Value)
                    : Task.FromResult<Resident>(null);

            // Complete both lookups before acting on either, so an early return (bad home id) or a
            // fault in one can never leave the other faulted and unobserved.
            try
            {
                await Task.WhenAll(homesTask, residentTask);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Deliberately empty: the individual awaits below rethrow with their own handling -
                // this join only guarantees both tasks are complete and observed before any early
                // return. Cancellation is excluded by the filter (which is what uses ex) so it
                // propagates immediately instead of reaching the per-task handling.
            }

            // Check that every requested id came back, rather than comparing counts: the question
            // being asked is "does each of these homes exist", and coverage answers it directly
            // without depending on the repository returning exactly one document per id.
            var existingHomes = await homesTask;
            var existingHomeIds = new HashSet<Guid>(existingHomes.Select(h => h.Id));
            if (requestedHomeIds.Any(id => !existingHomeIds.Contains(id)))
            {
                return BadRequest("One or more specified homes do not exist.");
            }

            // A linked resident must be an adult living in one of the homes assigned by this same
            // request (ResidentLinkRules is the single definition). An explicit request that
            // violates this is rejected; a *retained* link invalidated by the same request (home
            // unassigned, resident deleted or edited into a child record) is cleared instead - the
            // admin did not touch it, so there is nothing to reject.
            var residentLinkCleared = false;
            if (effectiveResidentId != null)
            {
                Resident linkedResident = null;
                var retainedLinkUnverifiable = false;
                try
                {
                    linkedResident = await residentTask;
                }
                catch (Exception ex) when (!explicitLinkChange && ex is not OperationCanceledException)
                {
                    // The admin did not touch the link, so its validation is hygiene, not
                    // load-bearing (readers tolerate a stale link) - a transient resident-read
                    // failure must not fail an otherwise roles/homes-only save. An explicit link
                    // change is different: it cannot be validated, so the exception propagates.
                    _logger.LogWarning(
                        ex,
                        "Could not verify retained resident link {ResidentId} for user {UserId}; leaving the link in place",
                        effectiveResidentId,
                        userToModify.UniqueId
                    );
                    retainedLinkUnverifiable = true;
                }

                if (!retainedLinkUnverifiable && !ResidentLinkRules.IsUsable(linkedResident, requestedHomeIds))
                {
                    if (explicitLinkChange)
                    {
                        return BadRequest("The linked resident must be an adult in one of the user's homes.");
                    }

                    effectiveResidentId = null;
                    residentLinkCleared = true;
                }
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
            userToModify.ResidentId = effectiveResidentId;

            // Write then audit, so the audit log only ever describes changes that really happened
            // (same reasoning as UserPurgeRunner's delete-then-audit ordering). The audit write is
            // best-effort for the same reason the purge's is: the change has already been applied,
            // so failing the request now would misreport a success and skip the signup conversion
            // below; the gap is visible in the error log and names the user.
            // A lost write race surfaces as ConcurrencyConflictException, which the global
            // ConcurrencyConflictExceptionFilter maps to the 409 refresh guidance.
            await _userRepository.UpsertAsync(userToModify);

            // Explicit link changes are called out so the audit log can answer "who redirected this
            // user's notification mail, and when" - the question a mis-delivery investigation asks.
            var linkNote = residentLinkCleared
                ? " Cleared the resident link, which is no longer valid for the assigned homes."
                : explicitLinkChange
                    ? effectiveResidentId != null
                        ? $" Set the resident link to {effectiveResidentId:D}."
                        : " Cleared the resident link."
                    : "";

            try
            {
                await _auditLogRepository.AddAsync(
                    new NewAuditLogEntry
                    {
                        Id = Guid.NewGuid(),
                        SubjectId = userToModify.UniqueId,
                        SubjectName = userToModify.Emails,
                        Action = "Updated user role and home associations." + linkNote,
                        Time = DateTime.UtcNow,
                        UserDisplayName = $"{apiUser.GivenName ?? ""} {apiUser.Surname ?? ""}",
                        UserId = apiUser.UniqueId,
                    }
                );
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(
                    ex,
                    "Failed to write the audit entry for an applied association update of user {UserId}",
                    userToModify.UniqueId
                );
            }

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
            var apiUser = await _currentUser.GetAsync(User);
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

        /// <summary>
        /// One-time migration: seeds <see cref="Models.User.ResidentId"/> for existing users by
        /// running the retired matching heuristic once (account email match first, then exact
        /// given+surname), preserving the person-level correlation the heuristic had been supplying.
        /// Delivery then follows the link semantics (the linked resident's address, preferring one
        /// that is also an account address of the user). Two deliberate narrowings versus the
        /// retired heuristic: only adults are linked (a link may never point at a child - correct a
        /// mis-typed record and re-run), and never overwrites an existing link. Safe to run multiple
        /// times (idempotent). Seeded links are visible and correctable in the manage-users editor.
        /// **Run this immediately after deploying the release that removes the heuristic** - until
        /// it runs, previously matched users receive digests at their account sign-in address.
        /// </summary>
        [HttpPost("admin/backfill-resident-links")]
        public async Task<IActionResult> BackfillResidentLinks()
        {
            var apiUser = await _currentUser.GetAsync(User);
            if (apiUser == null)
            {
                return NotFound();
            }

            var allUsers = await _userRepository.GetAllAsync();
            var candidates = allUsers
                .Where(u => u.ResidentId == null && u.OwnedHomeIds != null && u.OwnedHomeIds.Count > 0)
                .ToList();

            var homeIds = candidates.SelectMany(u => u.OwnedHomeIds).Distinct().ToList();
            var residents =
                homeIds.Count > 0
                    ? await _residentRepository.GetByHomeIdsAsync(homeIds)
                    : new List<Resident>();
            var residentsByHome = residents.GroupBy(r => r.HomeId).ToDictionary(g => g.Key, g => g.ToList());

            var result = new ResidentLinkBackfillResult { UsersConsidered = candidates.Count };
            foreach (var user in candidates)
            {
                var match = FindResidentMatch(user, residentsByHome);
                if (match == null)
                {
                    result.SkippedNoMatch++;
                    continue;
                }

                // Re-read just before writing, so the backfill's snapshot cannot revert a concurrent
                // admin edit; skip anyone whose link or homes changed since the snapshot - a rerun
                // picks up whoever is still unlinked. The fresh read also carries the ETag that
                // protects the write below.
                var fresh = await _userRepository.GetByUniqueIdAsync(user.UniqueId);
                if (
                    fresh == null
                    || fresh.ResidentId != null
                    || fresh.OwnedHomeIds == null
                    || !fresh.OwnedHomeIds.Contains(match.HomeId)
                )
                {
                    result.SkippedChangedDuringRun++;
                    continue;
                }

                fresh.ResidentId = match.Id;
                try
                {
                    await _userRepository.UpsertAsync(fresh);
                }
                catch (ConcurrencyConflictException)
                {
                    // Lost the race between the fresh read and the write: same outcome as the
                    // snapshot check above - a rerun picks the user up if they are still unlinked.
                    result.SkippedChangedDuringRun++;
                    continue;
                }

                result.Linked++;
            }

            // Best-effort: the link writes above have already been applied, so a failed audit write
            // must not turn a completed migration into a 500 that hides the result counts.
            try
            {
                await _auditLogRepository.AddAsync(
                    new NewAuditLogEntry
                    {
                        Id = Guid.NewGuid(),
                        SubjectId = "backfill-resident-links",
                        SubjectName = "Resident link backfill",
                        Action = $"Backfilled resident links: {result.Linked} linked, {result.SkippedNoMatch} left unlinked (no match), {result.SkippedChangedDuringRun} skipped (changed during run).",
                        Time = DateTime.UtcNow,
                        UserDisplayName = $"{apiUser.GivenName ?? ""} {apiUser.Surname ?? ""}".Trim(),
                        UserId = apiUser.UniqueId,
                    }
                );
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to write the audit entry for a completed resident-link backfill");
            }

            return Ok(result);
        }

        /// <summary>
        /// Mirrors the retired recipient-matching heuristic for the one-time backfill: the first
        /// adult resident in one of the user's homes listing one of the account's email addresses,
        /// else the first adult with an exactly matching given name and surname (case-insensitive).
        /// </summary>
        private static Resident FindResidentMatch(Models.User user, Dictionary<Guid, List<Resident>> residentsByHome)
        {
            var userGivenName = user.GivenName?.Trim();
            var userSurname = user.Surname?.Trim();

            foreach (var homeId in user.OwnedHomeIds)
            {
                if (!residentsByHome.TryGetValue(homeId, out var homeResidents))
                {
                    continue;
                }

                // Zero-id legacy records are excluded because the empty GUID is the wire protocol's
                // clear sentinel and can never resolve as a link target.
                var adults = homeResidents
                    .Where(r => r.ResidentType != Resident.Type.Child && r.Id != Guid.Empty)
                    .ToList();

                foreach (var userAddress in UserEmailHelpers.SplitEmails(user.Emails))
                {
                    var emailMatch = adults.FirstOrDefault(r =>
                        r.EmailAddresses != null
                        && r.EmailAddresses.Any(e =>
                            string.Equals(e.Address?.Trim(), userAddress, StringComparison.OrdinalIgnoreCase)
                        )
                    );
                    if (emailMatch != null)
                    {
                        return emailMatch;
                    }
                }

                // Name matching requires both names non-blank: string.Equals(null, null) is true, so
                // without this an account missing a name would link to any half-entered resident record.
                if (string.IsNullOrWhiteSpace(userGivenName) || string.IsNullOrWhiteSpace(userSurname))
                {
                    continue;
                }

                var nameMatch = adults.FirstOrDefault(r =>
                    string.Equals(r.GivenName?.Trim(), userGivenName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(r.Surname?.Trim(), userSurname, StringComparison.OrdinalIgnoreCase)
                );
                if (nameMatch != null)
                {
                    return nameMatch;
                }
            }

            return null;
        }
    }

    /// <summary>Result of the one-time resident-link backfill.</summary>
    public sealed class ResidentLinkBackfillResult
    {
        public int UsersConsidered { get; set; }

        public int Linked { get; set; }

        public int SkippedNoMatch { get; set; }

        /// <summary>Users skipped because their document changed between snapshot and write.</summary>
        public int SkippedChangedDuringRun { get; set; }
    }
}
