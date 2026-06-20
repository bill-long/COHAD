#nullable enable
using System.Threading;
using System.Threading.Tasks;

namespace Web.Services
{
    /// <summary>
    /// Pushes a detail-free "your notifications changed" signal to an audience so connected clients
    /// re-fetch the authorized list. Kept as a seam so <see cref="NotificationService"/> stays
    /// independent of the SignalR transport; the real-time (hub-backed) implementation is wired in a
    /// later phase, with <see cref="NoOpNotificationRealtimeNotifier"/> as the default until then.
    /// </summary>
    public interface INotificationRealtimeNotifier
    {
        Task NotifyAudienceChangedAsync(string audienceKey, CancellationToken ct = default);
    }

    /// <summary>Default notifier that does nothing. Persistence still works; clients pick up changes on their next fetch.</summary>
    public sealed class NoOpNotificationRealtimeNotifier : INotificationRealtimeNotifier
    {
        public Task NotifyAudienceChangedAsync(string audienceKey, CancellationToken ct = default) => Task.CompletedTask;
    }
}
