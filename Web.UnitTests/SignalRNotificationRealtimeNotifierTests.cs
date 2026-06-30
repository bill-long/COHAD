using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Web.Hubs;
using Web.Models;
using Web.Services;
using Xunit;

namespace Web.UnitTests;

public sealed class SignalRNotificationRealtimeNotifierTests
{
    [Fact]
    public async Task NotifyAudienceChangedAsync_broadcasts_detail_free_event_to_the_audience_group()
    {
        var proxy = new Mock<IClientProxy>();
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(NotificationAudience.Administrators)).Returns(proxy.Object);
        var hubContext = new Mock<IHubContext<NotificationsHub>>();
        hubContext.Setup(h => h.Clients).Returns(clients.Object);

        var notifier = new SignalRNotificationRealtimeNotifier(hubContext.Object);
        await notifier.NotifyAudienceChangedAsync(NotificationAudience.Administrators);

        clients.Verify(c => c.Group(NotificationAudience.Administrators), Times.Once);
        // Detail-free: the method name only, no payload args (SendAsync(method, ct) → SendCoreAsync(method, [], ct)).
        proxy.Verify(
            p => p.SendCoreAsync(
                SignalRNotificationRealtimeNotifier.ChangedEventName,
                It.Is<object[]>(args => args.Length == 0),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task NotifyAudienceChangedAsync_ignores_an_empty_audience_key()
    {
        var clients = new Mock<IHubClients>(MockBehavior.Strict);
        var hubContext = new Mock<IHubContext<NotificationsHub>>();
        hubContext.Setup(h => h.Clients).Returns(clients.Object);

        var notifier = new SignalRNotificationRealtimeNotifier(hubContext.Object);
        await notifier.NotifyAudienceChangedAsync(string.Empty);

        // Strict mock would throw on any Group(...) call; reaching here proves no broadcast was attempted.
        clients.Verify(c => c.Group(It.IsAny<string>()), Times.Never);
    }
}
