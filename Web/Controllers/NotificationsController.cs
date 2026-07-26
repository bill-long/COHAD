#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Web.Models;
using Web.PresentationModels;
using Web.Services;
using Web.Services.Repositories;

namespace Web.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class NotificationsController : ControllerBase
    {
        private readonly INotificationService _notificationService;
        private readonly IUserRepository _userRepository;
        private readonly CommitteeListCache _committeeListCache;

        public NotificationsController(
            INotificationService notificationService,
            IUserRepository userRepository,
            CommitteeListCache committeeListCache
        )
        {
            _notificationService = notificationService;
            _userRepository = userRepository;
            _committeeListCache = committeeListCache;
        }

        /// <summary>
        /// Unresolved in-app notifications for every audience the caller belongs to
        /// (the Administrators audience for Administrators, plus each committee the caller can moderate),
        /// newest first.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetMine()
        {
            var apiUser = await GetApiUserAsync();
            if (apiUser == null)
                return Forbid();

            var audiences = await ResolveAudiencesAsync(apiUser);
            if (audiences.Count == 0)
                return Ok(Array.Empty<NotificationPresentation>());

            // The per-audience queries are independent — run them in parallel, then merge and sort.
            var perAudience = await Task.WhenAll(
                audiences.Select(a => _notificationService.GetUnresolvedForAudienceAsync(a))
            );

            var payload = perAudience
                .SelectMany(list => list)
                .OrderByDescending(n => n.CreatedUtc)
                .Select(NotificationPresentation.FromStorageModel)
                .ToList();

            return Ok(payload);
        }

        /// <summary>
        /// Explicitly acknowledges (resolves) a notification that has no other resolving action —
        /// today, new-user registrations. Notifications backed by a moderation action (vendor flags,
        /// held emails) are resolved by that action instead.
        /// </summary>
        [HttpPost("{id:guid}/acknowledge")]
        [Authorize(Policy = "Administrator")]
        public async Task<IActionResult> Acknowledge(Guid id)
        {
            var apiUser = await GetApiUserAsync();
            if (apiUser == null)
                return Forbid();

            var existing = await _notificationService.GetByIdAsync(id);
            if (existing == null)
                return NotFound();

            var audiences = await ResolveAudiencesAsync(apiUser);
            if (!audiences.Contains(existing.AudienceKey, StringComparer.Ordinal))
                return Forbid();

            // Only types with no underlying moderation action may be acknowledged. Vendor flags and
            // held emails must be resolved by dismissing/approving/rejecting them — acknowledging here
            // would mark the notification resolved while the actual work is still pending.
            if (!Notification.IsAcknowledgeable(existing.Type))
                return BadRequest($"Notifications of type {existing.Type} are resolved by their moderation action, not by acknowledgement.");

            var acknowledged = await _notificationService.AcknowledgeAsync(id, apiUser.UniqueId);
            if (acknowledged == null)
                return NotFound();

            return Ok(NotificationPresentation.FromStorageModel(acknowledged));
        }

        /// <summary>
        /// Undoes an acknowledgement, re-opening the notification for its whole audience. Exists so an
        /// accidental click on the bell's "mark as handled" action is recoverable (the client offers a
        /// brief undo). Restricted to the same types that can be acknowledged; notifications backed by
        /// a moderation action are rejected with 400, mirroring <see cref="Acknowledge"/>.
        /// </summary>
        [HttpPost("{id:guid}/unacknowledge")]
        [Authorize(Policy = "Administrator")]
        public async Task<IActionResult> Unacknowledge(Guid id)
        {
            var apiUser = await GetApiUserAsync();
            if (apiUser == null)
                return Forbid();

            var existing = await _notificationService.GetByIdAsync(id);
            if (existing == null)
                return NotFound();

            var audiences = await ResolveAudiencesAsync(apiUser);
            if (!audiences.Contains(existing.AudienceKey, StringComparer.Ordinal))
                return Forbid();

            if (!Notification.IsAcknowledgeable(existing.Type))
                return BadRequest($"Notifications of type {existing.Type} are resolved by their moderation action and cannot be re-opened here.");

            var reopened = await _notificationService.UnacknowledgeAsync(id);
            if (reopened == null)
                return NotFound();

            return Ok(NotificationPresentation.FromStorageModel(reopened));
        }

        private async Task<List<string>> ResolveAudiencesAsync(User user)
        {
            // Shared with NotificationsHub group membership (same resolver, same cached committee list)
            // so the badge/list and the live push channel always agree on who sees a notification.
            var committees = await _committeeListCache.GetAllAsync();
            return NotificationAudienceResolver.Resolve(user, committees).ToList();
        }

        private async Task<User?> GetApiUserAsync()
        {
            var uniqueId = Models.User.GetUniqueIdFromClaims(User.Claims);
            return await _userRepository.GetByUniqueIdAsync(uniqueId);
        }
    }
}
