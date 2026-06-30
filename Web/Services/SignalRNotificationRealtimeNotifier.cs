#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Web.Hubs;

namespace Web.Services
{
    /// <summary>
    /// SignalR-backed <see cref="INotificationRealtimeNotifier"/>: broadcasts a detail-free
    /// "<see cref="ChangedEventName"/>" event to the audience's group on <see cref="NotificationsHub"/>
    /// (whose group names are the audience keys). Connected clients re-fetch the authorized list in
    /// response — no notification content is pushed, so a connection whose owner's rights changed after
    /// connecting cannot receive details it shouldn't see. Best-effort by contract: see
    /// <see cref="NotificationService"/>, which swallows signal failures so a live-push problem never
    /// fails the persisted change.
    /// </summary>
    public sealed class SignalRNotificationRealtimeNotifier : INotificationRealtimeNotifier
    {
        /// <summary>Client-side method name invoked on the signal. Detail-free by design.</summary>
        public const string ChangedEventName = "NotificationsChanged";

        private readonly IHubContext<NotificationsHub> _hub;

        public SignalRNotificationRealtimeNotifier(IHubContext<NotificationsHub> hub)
        {
            _hub = hub;
        }

        public Task NotifyAudienceChangedAsync(string audienceKey, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(audienceKey))
                return Task.CompletedTask;

            return _hub.Clients.Group(audienceKey).SendAsync(ChangedEventName, ct);
        }
    }
}
