#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Web.Services;
using Web.Services.Repositories;

namespace Web.Hubs
{
    /// <summary>
    /// Pushes a detail-free "your notifications changed" signal to the people who can act on a unified
    /// in-app notification. On connect each caller joins one SignalR group per audience key they belong
    /// to (<see cref="NotificationAudienceResolver"/>): the Administrators audience for an Administrator,
    /// plus each committee they moderate. The group name is the audience key itself, so
    /// <see cref="SignalRNotificationRealtimeNotifier"/> can broadcast straight to it. Clients re-fetch
    /// the authorized list on the signal, so message details never flow to a connection whose owner's
    /// rights changed after connecting.
    /// </summary>
    /// <remarks>
    /// Group membership is resolved at connection time. A change to a user's roles or a committee's
    /// management role takes effect on the next reconnect. The <c>CommitteeEditor</c> policy gates the
    /// connection to exactly the roles that can hold an audience (Administrators + committee roles), so a
    /// plain resident never opens a (group-less) connection.
    /// </remarks>
    [Authorize(Policy = "CommitteeEditor")]
    public class NotificationsHub : Hub
    {
        private readonly IUserRepository _userRepository;
        private readonly CommitteeListCache _committeeListCache;
        private readonly ILogger<NotificationsHub> _logger;

        public NotificationsHub(
            IUserRepository userRepository,
            CommitteeListCache committeeListCache,
            ILogger<NotificationsHub> logger
        )
        {
            _userRepository = userRepository;
            _committeeListCache = committeeListCache;
            _logger = logger;
        }

        public override async Task OnConnectedAsync()
        {
            try
            {
                // Fully qualified to disambiguate from Context.User (the ClaimsPrincipal).
                var uniqueId = Web.Models.User.GetUniqueIdFromClaims(
                    Context.User?.Claims ?? Enumerable.Empty<System.Security.Claims.Claim>());

                // The user fetch and committee list are independent — issue both and await together.
                var userTask = _userRepository.GetByUniqueIdAsync(uniqueId);
                var committeesTask = _committeeListCache.GetAllAsync();
                await Task.WhenAll(userTask, committeesTask);

                foreach (var audienceKey in NotificationAudienceResolver.Resolve(await userTask, await committeesTask))
                {
                    await Groups.AddToGroupAsync(Context.ConnectionId, audienceKey);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Abort so the client's automatic reconnect retries group assignment, rather than sitting
                // on a "healthy" connection that silently belongs to no groups (and so would receive no
                // live signals until a full reload).
                _logger.LogWarning(ex, "Failed to assign notification groups for connection {ConnectionId}; aborting so the client reconnects", Context.ConnectionId);
                Context.Abort();
                return;
            }

            await base.OnConnectedAsync();
        }

        // SignalR removes a connection from all its groups automatically on disconnect, so no explicit
        // group cleanup is needed here.
    }
}
