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
        private readonly ICommitteeRepository _committeeRepository;

        public NotificationsController(
            INotificationService notificationService,
            IUserRepository userRepository,
            ICommitteeRepository committeeRepository
        )
        {
            _notificationService = notificationService;
            _userRepository = userRepository;
            _committeeRepository = committeeRepository;
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

            var collected = new List<Notification>();
            foreach (var audience in audiences)
                collected.AddRange(await _notificationService.GetUnresolvedForAudienceAsync(audience));

            var payload = collected
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

        private async Task<List<string>> ResolveAudiencesAsync(User user)
        {
            var audiences = new List<string>();
            if (user.Roles?.Contains(Models.User.Role.Administrator) == true)
                audiences.Add(NotificationAudience.Administrators);

            var committees = await _committeeRepository.GetAllAsync();
            foreach (var committee in committees)
            {
                if (CommitteeAuthorization.CanManage(user, committee))
                    audiences.Add(NotificationAudience.Committee(committee.Id));
            }

            return audiences;
        }

        private async Task<User?> GetApiUserAsync()
        {
            var uniqueId = Models.User.GetUniqueIdFromClaims(User.Claims);
            return await _userRepository.GetByUniqueIdAsync(uniqueId);
        }
    }
}
