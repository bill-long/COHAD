#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Web.Models;
using Web.Services;
using Web.Services.Repositories;

namespace Web.Hubs
{
    /// <summary>
    /// Pushes held-message moderation notifications to the people who can act on them.
    /// On connect, each caller is added to a SignalR group per committee they manage
    /// (an Administrator manages every committee; a committee-role holder manages the
    /// committees whose <see cref="Committee.ManagementRole"/> matches one of their roles).
    /// The poller and the approve/reject endpoints then broadcast to a single committee
    /// group, so every eligible moderator — and only them — is notified exactly once.
    /// </summary>
    /// <remarks>
    /// Group membership is resolved at connection time. A change to a user's roles or a
    /// committee's <see cref="Committee.ManagementRole"/> takes effect on the next reconnect.
    /// </remarks>
    [Authorize(Policy = "CommitteeEditor")]
    public class HeldMessageNotificationsHub : Hub
    {
        private readonly IUserRepository _userRepository;
        private readonly CommitteeListCache _committeeListCache;
        private readonly ILogger<HeldMessageNotificationsHub> _logger;

        public HeldMessageNotificationsHub(
            IUserRepository userRepository,
            CommitteeListCache committeeListCache,
            ILogger<HeldMessageNotificationsHub> logger
        )
        {
            _userRepository = userRepository;
            _committeeListCache = committeeListCache;
            _logger = logger;
        }

        /// <summary>The SignalR group that receives notifications for a single committee.</summary>
        public static string CommitteeGroupName(string committeeId) => $"held:committee:{committeeId}";

        /// <summary>
        /// Returns the ids of committees the user may moderate: all committees for an
        /// Administrator, otherwise those whose <see cref="Committee.ManagementRole"/> is one
        /// of the user's roles. Pure so it can be unit-tested without a live connection.
        /// </summary>
        public static IReadOnlyList<string> ResolveManagedCommitteeIds(
            User? user,
            IEnumerable<Committee> committees
        )
        {
            if (committees == null)
                return Array.Empty<string>();

            return committees
                .Where(c => c != null && !string.IsNullOrEmpty(c.Id) && CommitteeAuthorization.CanManage(user, c))
                .Select(c => c.Id)
                .Distinct()
                .ToList();
        }

        public override async Task OnConnectedAsync()
        {
            try
            {
                // Fully qualified to disambiguate from Context.User (the ClaimsPrincipal).
                var uniqueId = Web.Models.User.GetUniqueIdFromClaims(Context.User?.Claims ?? Enumerable.Empty<System.Security.Claims.Claim>());
                var apiUser = await _userRepository.GetByUniqueIdAsync(uniqueId);
                var committees = await _committeeListCache.GetAllAsync();

                foreach (var committeeId in ResolveManagedCommitteeIds(apiUser, committees))
                {
                    await Groups.AddToGroupAsync(Context.ConnectionId, CommitteeGroupName(committeeId));
                }
            }
            catch (Exception ex)
            {
                // Abort so the client's automatic reconnect retries group assignment, rather than
                // sitting on a "healthy" connection that silently belongs to no groups (and so
                // would receive no live notifications until a full reload).
                _logger.LogWarning(ex, "Failed to assign held-message notification groups for connection {ConnectionId}; aborting so the client reconnects", Context.ConnectionId);
                Context.Abort();
                return;
            }

            await base.OnConnectedAsync();
        }

        // SignalR removes a connection from all its groups automatically on disconnect,
        // so no explicit group cleanup is needed here.
    }
}
