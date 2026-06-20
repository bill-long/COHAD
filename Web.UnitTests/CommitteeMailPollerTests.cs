using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Graph.Models;
using Moq;
using Web.Hubs;
using Web.Models;
using Web.Services;
using Web.Services.Repositories;
using Xunit;

namespace Web.UnitTests;

public sealed class CommitteeMailPollerTests
{
    [Fact]
    public async Task PollAllCommittees_holding_unknown_sender_raises_committee_notification()
    {
        var committee = new Committee
        {
            Id = "board",
            DisplayName = "Board",
            CommitteeEmail = "board@cohad.org",
            ForwardingEnabled = true,
            ForwardingSenderFilter = ForwardingSenderFilter.DirectoryOnly,
            Members = new List<CommitteeMember>
            {
                new CommitteeMember { ResidentId = Guid.NewGuid(), ReceivesForwardedEmail = true },
            },
        };

        var committeeRepo = new Mock<ICommitteeRepository>();
        committeeRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Committee> { committee });
        committeeRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);
        committeeRepo.Setup(r => r.UpsertAsync(It.IsAny<Committee>())).ReturnsAsync((Committee c) => c);

        var emailJobRepo = new Mock<IEmailJobRepository>();
        emailJobRepo.Setup(r => r.GetByInternetMessageIdAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync((EmailJob?)null);

        var heldRepo = new Mock<IHeldMessageRepository>();
        heldRepo.Setup(r => r.GetByInternetMessageIdAsync("board", It.IsAny<string>())).ReturnsAsync((HeldMessage?)null);
        heldRepo.Setup(r => r.AddAsync(It.IsAny<HeldMessage>())).Returns(Task.CompletedTask);

        var residentRepo = new Mock<IResidentRepository>();
        residentRepo.Setup(r => r.GetByIdsAsync(It.IsAny<IEnumerable<Guid>>())).ReturnsAsync(new List<Resident>());
        residentRepo.Setup(r => r.GetByEmailAsync(It.IsAny<string>())).ReturnsAsync(new List<Resident>()); // unknown sender → hold

        var notifications = new Mock<INotificationService>();
        notifications
            .Setup(s => s.RaiseAsync(
                It.IsAny<NotificationType>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Notification());

        var services = new ServiceCollection();
        services.AddScoped(_ => committeeRepo.Object);
        services.AddScoped(_ => emailJobRepo.Object);
        services.AddScoped(_ => heldRepo.Object);
        services.AddScoped(_ => residentRepo.Object);
        services.AddScoped(_ => Mock.Of<IUserRepository>());
        services.AddScoped(_ => Mock.Of<IDocumentFileStore>());
        services.AddScoped(_ => notifications.Object);
        var provider = services.BuildServiceProvider();

        var message = new Message
        {
            Id = "graph-1",
            InternetMessageId = "<msg-1@example.com>",
            Subject = "Hello",
            ReceivedDateTime = DateTimeOffset.UtcNow,
            From = new Recipient { EmailAddress = new Microsoft.Graph.Models.EmailAddress { Address = "stranger@example.com", Name = "Stranger" } },
        };

        var graphReader = new Mock<IGraphMailReader>();
        graphReader.Setup(g => g.GetInboxMessagesAsync("board@cohad.org", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Message> { message });
        graphReader.Setup(g => g.GetOrCreateFolderAsync("board@cohad.org", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("processed-folder");

        var hubProxy = new Mock<IClientProxy>();
        hubProxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var hubClients = new Mock<IHubClients>();
        hubClients.Setup(c => c.Group(It.IsAny<string>())).Returns(hubProxy.Object);
        var hubContext = new Mock<IHubContext<HeldMessageNotificationsHub>>();
        hubContext.Setup(h => h.Clients).Returns(hubClients.Object);

        var config = new ConfigurationBuilder().Build();
        var poller = new CommitteeMailPoller(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new EmailJobQueue(),
            graphReader.Object,
            hubContext.Object,
            config,
            NullLogger<CommitteeMailPoller>.Instance
        );

        await poller.PollAllCommitteesAsync(CancellationToken.None);

        heldRepo.Verify(r => r.AddAsync(It.IsAny<HeldMessage>()), Times.Once);
        notifications.Verify(s => s.RaiseAsync(
            NotificationType.HeldMessage,
            NotificationAudience.Committee("board"),
            NotificationTargetType.HeldMessage,
            It.IsAny<string>(),
            "Held committee email",
            It.Is<string>(summary => summary.Contains("Board") && summary.Contains("Stranger") && summary.Contains("Hello")),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
