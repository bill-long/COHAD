using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Graph.Models;
using Moq;
using Web.Models;
using Web.Services;
using Web.Services.Repositories;
using Xunit;

namespace Web.UnitTests;

public sealed class CommitteeMailPollerTests
{
    [Fact]
    public async Task PollAllCommittees_holding_unknown_sender_holds_without_notifying()
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
        // Nothing is past its antispam window this cycle, so the quarantine sweep finds nothing.
        heldRepo.Setup(r => r.GetAwaitingNotificationAsync(It.IsAny<DateTime>(), It.IsAny<int>()))
            .ReturnsAsync(new List<HeldMessage>());

        var residentRepo = new Mock<IResidentRepository>();
        residentRepo.Setup(r => r.GetByIdsAsync(It.IsAny<IEnumerable<Guid>>())).ReturnsAsync(new List<Resident>());
        residentRepo.Setup(r => r.GetByEmailAsync(It.IsAny<string>())).ReturnsAsync(new List<Resident>()); // unknown sender → hold

        var notifications = new Mock<INotificationService>();
        notifications
            .Setup(s => s.RaiseAsync(
                It.IsAny<NotificationType>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
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

        var config = new ConfigurationBuilder().Build();
        var poller = new CommitteeMailPoller(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new EmailJobQueue(),
            graphReader.Object,
            config,
            NullLogger<CommitteeMailPoller>.Instance
        );

        await poller.PollAllCommitteesAsync(CancellationToken.None);

        // The message is held (and kept in NotifiedUtc-null quarantine) but no one is notified yet:
        // notification is deferred to the antispam sweep once the hold window elapses.
        heldRepo.Verify(r => r.AddAsync(It.Is<HeldMessage>(m => m.NotifiedUtc == null)), Times.Once);
        notifications.Verify(s => s.RaiseAsync(
            It.IsAny<NotificationType>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PollAllCommittees_notifies_held_message_once_past_antispam_hold()
    {
        var committee = new Committee
        {
            Id = "board",
            DisplayName = "Board",
            CommitteeEmail = "board@cohad.org",
            // Forwarding disabled: only the antispam quarantine sweep should run this cycle.
            ForwardingEnabled = false,
        };

        var committeeRepo = new Mock<ICommitteeRepository>();
        committeeRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Committee> { committee });

        var held = new HeldMessage
        {
            Id = Guid.NewGuid(),
            CommitteeId = "board",
            CommitteeEmail = "board@cohad.org",
            InternetMessageId = "<msg-1@example.com>",
            SenderEmail = "stranger@example.com",
            SenderName = "Stranger",
            Subject = "Hello",
            ReceivedUtc = DateTime.UtcNow.AddHours(-2),
            HeldUtc = DateTime.UtcNow.AddHours(-2),
            Status = HeldMessageStatus.Held,
            NotifiedUtc = null,
            ETag = "1",
        };

        var heldRepo = new Mock<IHeldMessageRepository>();
        heldRepo.Setup(r => r.GetAwaitingNotificationAsync(It.IsAny<DateTime>(), It.IsAny<int>()))
            .ReturnsAsync(new List<HeldMessage> { held });
        // Success path acts on the query row directly (no re-read); GetByIdAsync is only used on conflict.
        heldRepo.Setup(r => r.UpdateAsync(It.IsAny<HeldMessage>())).Returns(Task.CompletedTask);

        var notifications = new Mock<INotificationService>();
        notifications
            .Setup(s => s.RaiseAsync(
                It.IsAny<NotificationType>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Notification());

        var services = new ServiceCollection();
        services.AddScoped(_ => committeeRepo.Object);
        services.AddScoped(_ => Mock.Of<IEmailJobRepository>());
        services.AddScoped(_ => heldRepo.Object);
        services.AddScoped(_ => Mock.Of<IResidentRepository>());
        services.AddScoped(_ => Mock.Of<IUserRepository>());
        services.AddScoped(_ => Mock.Of<IDocumentFileStore>());
        services.AddScoped(_ => notifications.Object);
        var provider = services.BuildServiceProvider();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["CommitteeForwarding:AntispamHoldMinutes"] = "60" })
            .Build();
        var poller = new CommitteeMailPoller(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new EmailJobQueue(),
            Mock.Of<IGraphMailReader>(),
            config,
            NullLogger<CommitteeMailPoller>.Instance
        );

        await poller.PollAllCommitteesAsync(CancellationToken.None);

        notifications.Verify(s => s.RaiseAsync(
            NotificationType.HeldMessage,
            NotificationAudience.Committee("board"),
            NotificationTargetType.HeldMessage,
            held.Id.ToString("D"),
            "Held committee email",
            It.Is<string>(summary => summary.Contains("Board") && summary.Contains("Stranger") && summary.Contains("Hello")),
            It.Is<string>(deepLink => IsApprovalsDeepLinkWithGuid(deepLink)),
            It.IsAny<CancellationToken>()), Times.Once);
        // Stamped notified so a later sweep won't re-notify.
        heldRepo.Verify(r => r.UpdateAsync(It.Is<HeldMessage>(m => m.Id == held.Id && m.NotifiedUtc != null)), Times.Once);
        // The cutoff is derived from the configured window (UtcNow - 60 min), not "now" — a sign error or
        // ignoring the hold would query with a ~now cutoff and defeat the quarantine.
        heldRepo.Verify(
            r => r.GetAwaitingNotificationAsync(
                It.Is<DateTime>(cutoff =>
                    cutoff <= DateTime.UtcNow - TimeSpan.FromMinutes(59)
                    && cutoff >= DateTime.UtcNow - TimeSpan.FromMinutes(61)),
                It.IsAny<int>()),
            Times.Once);
    }

    [Fact]
    public async Task PollAllCommittees_notifies_administrators_when_held_message_has_no_committee_id()
    {
        var committeeRepo = new Mock<ICommitteeRepository>();
        committeeRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Committee>());

        // A legacy/corrupt held record with no CommitteeId must not throw on the committee lookup and
        // must not produce an unresolvable "committee:" audience — it falls back to Administrators.
        var held = new HeldMessage
        {
            Id = Guid.NewGuid(),
            CommitteeId = null,
            CommitteeEmail = "board@cohad.org",
            SenderName = "Stranger",
            Subject = "Hello",
            HeldUtc = DateTime.UtcNow.AddHours(-2),
            Status = HeldMessageStatus.Held,
            ETag = "1",
        };

        var heldRepo = new Mock<IHeldMessageRepository>();
        heldRepo.Setup(r => r.GetAwaitingNotificationAsync(It.IsAny<DateTime>(), It.IsAny<int>()))
            .ReturnsAsync(new List<HeldMessage> { held });
        heldRepo.Setup(r => r.UpdateAsync(It.IsAny<HeldMessage>())).Returns(Task.CompletedTask);

        var notifications = new Mock<INotificationService>();
        notifications
            .Setup(s => s.RaiseAsync(
                It.IsAny<NotificationType>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Notification());

        var services = new ServiceCollection();
        services.AddScoped(_ => committeeRepo.Object);
        services.AddScoped(_ => Mock.Of<IEmailJobRepository>());
        services.AddScoped(_ => heldRepo.Object);
        services.AddScoped(_ => Mock.Of<IResidentRepository>());
        services.AddScoped(_ => Mock.Of<IUserRepository>());
        services.AddScoped(_ => Mock.Of<IDocumentFileStore>());
        services.AddScoped(_ => notifications.Object);
        var provider = services.BuildServiceProvider();

        var poller = new CommitteeMailPoller(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new EmailJobQueue(),
            Mock.Of<IGraphMailReader>(),
            new ConfigurationBuilder().Build(),
            NullLogger<CommitteeMailPoller>.Instance
        );

        await poller.PollAllCommitteesAsync(CancellationToken.None);

        notifications.Verify(s => s.RaiseAsync(
            NotificationType.HeldMessage,
            NotificationAudience.Administrators,
            NotificationTargetType.HeldMessage,
            held.Id.ToString("D"),
            "Held committee email",
            It.Is<string>(summary => summary.Contains("board@cohad.org") && summary.Contains("Stranger")),
            It.Is<string>(deepLink => IsApprovalsDeepLinkWithGuid(deepLink)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PollAllCommittees_resolves_phantom_notification_when_message_actioned_during_stamp()
    {
        var committee = new Committee
        {
            Id = "board",
            DisplayName = "Board",
            CommitteeEmail = "board@cohad.org",
            ForwardingEnabled = false,
        };

        var committeeRepo = new Mock<ICommitteeRepository>();
        committeeRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Committee> { committee });

        var candidate = new HeldMessage
        {
            Id = Guid.NewGuid(),
            CommitteeId = "board",
            CommitteeEmail = "board@cohad.org",
            Subject = "Hello",
            Status = HeldMessageStatus.Held,
            ETag = "1",
        };
        // A moderator approves the message concurrently: the stamp loses the ETag race, and a re-read
        // shows it is no longer Held.
        var actioned = new HeldMessage { Id = candidate.Id, CommitteeId = "board", Status = HeldMessageStatus.Approved };

        var heldRepo = new Mock<IHeldMessageRepository>();
        heldRepo.Setup(r => r.GetAwaitingNotificationAsync(It.IsAny<DateTime>(), It.IsAny<int>()))
            .ReturnsAsync(new List<HeldMessage> { candidate });
        heldRepo.Setup(r => r.UpdateAsync(It.IsAny<HeldMessage>()))
            .ThrowsAsync(new InvalidOperationException("HeldMessage was modified by another process."));
        heldRepo.Setup(r => r.GetByIdAsync(candidate.Id)).ReturnsAsync(actioned);

        var notifications = new Mock<INotificationService>();
        notifications
            .Setup(s => s.RaiseAsync(
                It.IsAny<NotificationType>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Notification());
        notifications
            .Setup(s => s.ResolveAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Notification?)null);

        var services = new ServiceCollection();
        services.AddScoped(_ => committeeRepo.Object);
        services.AddScoped(_ => Mock.Of<IEmailJobRepository>());
        services.AddScoped(_ => heldRepo.Object);
        services.AddScoped(_ => Mock.Of<IResidentRepository>());
        services.AddScoped(_ => Mock.Of<IUserRepository>());
        services.AddScoped(_ => Mock.Of<IDocumentFileStore>());
        services.AddScoped(_ => notifications.Object);
        var provider = services.BuildServiceProvider();

        var poller = new CommitteeMailPoller(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new EmailJobQueue(),
            Mock.Of<IGraphMailReader>(),
            new ConfigurationBuilder().Build(),
            NullLogger<CommitteeMailPoller>.Instance
        );

        await poller.PollAllCommitteesAsync(CancellationToken.None);

        // The notification is raised first...
        notifications.Verify(s => s.RaiseAsync(
            NotificationType.HeldMessage, NotificationAudience.Committee("board"), NotificationTargetType.HeldMessage,
            candidate.Id.ToString("D"), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
        // ...then compensated (resolved) because the message was actioned concurrently — so it never
        // escalates as an alert for an already-handled message. Reason is the contract value.
        notifications.Verify(s => s.ResolveAsync(
            NotificationTargetType.HeldMessage, candidate.Id.ToString("D"), "system:antispam-hold", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PollAllCommittees_resolves_phantom_notification_when_message_deleted_during_stamp()
    {
        var committee = new Committee { Id = "board", DisplayName = "Board", CommitteeEmail = "board@cohad.org", ForwardingEnabled = false };

        var committeeRepo = new Mock<ICommitteeRepository>();
        committeeRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Committee> { committee });

        var candidate = new HeldMessage
        {
            Id = Guid.NewGuid(),
            CommitteeId = "board",
            CommitteeEmail = "board@cohad.org",
            Subject = "Hello",
            Status = HeldMessageStatus.Held,
            ETag = "1",
        };

        var heldRepo = new Mock<IHeldMessageRepository>();
        heldRepo.Setup(r => r.GetAwaitingNotificationAsync(It.IsAny<DateTime>(), It.IsAny<int>()))
            .ReturnsAsync(new List<HeldMessage> { candidate });
        heldRepo.Setup(r => r.UpdateAsync(It.IsAny<HeldMessage>()))
            .ThrowsAsync(new InvalidOperationException("HeldMessage was modified by another process."));
        // The re-read finds nothing — the message was deleted concurrently.
        heldRepo.Setup(r => r.GetByIdAsync(candidate.Id)).ReturnsAsync((HeldMessage?)null);

        var notifications = new Mock<INotificationService>();
        notifications
            .Setup(s => s.RaiseAsync(
                It.IsAny<NotificationType>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Notification());
        notifications
            .Setup(s => s.ResolveAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Notification?)null);

        var services = new ServiceCollection();
        services.AddScoped(_ => committeeRepo.Object);
        services.AddScoped(_ => Mock.Of<IEmailJobRepository>());
        services.AddScoped(_ => heldRepo.Object);
        services.AddScoped(_ => Mock.Of<IResidentRepository>());
        services.AddScoped(_ => Mock.Of<IUserRepository>());
        services.AddScoped(_ => Mock.Of<IDocumentFileStore>());
        services.AddScoped(_ => notifications.Object);
        var provider = services.BuildServiceProvider();

        var poller = new CommitteeMailPoller(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new EmailJobQueue(),
            Mock.Of<IGraphMailReader>(),
            new ConfigurationBuilder().Build(),
            NullLogger<CommitteeMailPoller>.Instance
        );

        await poller.PollAllCommitteesAsync(CancellationToken.None);

        // A concurrently-deleted message (re-read == null) is also treated as no-longer-Held, so the
        // raised notification is resolved rather than left to escalate.
        notifications.Verify(s => s.ResolveAsync(
            NotificationTargetType.HeldMessage, candidate.Id.ToString("D"), "system:antispam-hold", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PollAllCommittees_continues_sweep_when_one_message_raise_throws()
    {
        var committee = new Committee { Id = "board", DisplayName = "Board", CommitteeEmail = "board@cohad.org", ForwardingEnabled = false };

        var committeeRepo = new Mock<ICommitteeRepository>();
        committeeRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Committee> { committee });

        var failing = new HeldMessage { Id = Guid.NewGuid(), CommitteeId = "board", CommitteeEmail = "board@cohad.org", Subject = "Boom", Status = HeldMessageStatus.Held, ETag = "1" };
        var ok = new HeldMessage { Id = Guid.NewGuid(), CommitteeId = "board", CommitteeEmail = "board@cohad.org", Subject = "Fine", Status = HeldMessageStatus.Held, ETag = "1" };

        var heldRepo = new Mock<IHeldMessageRepository>();
        heldRepo.Setup(r => r.GetAwaitingNotificationAsync(It.IsAny<DateTime>(), It.IsAny<int>()))
            .ReturnsAsync(new List<HeldMessage> { failing, ok });
        heldRepo.Setup(r => r.UpdateAsync(It.IsAny<HeldMessage>())).Returns(Task.CompletedTask);

        var notifications = new Mock<INotificationService>();
        // The first message's raise throws; the sweep must swallow it and still process the second.
        notifications
            .Setup(s => s.RaiseAsync(
                It.IsAny<NotificationType>(), It.IsAny<string>(), It.IsAny<string>(),
                failing.Id.ToString("D"), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        notifications
            .Setup(s => s.RaiseAsync(
                It.IsAny<NotificationType>(), It.IsAny<string>(), It.IsAny<string>(),
                ok.Id.ToString("D"), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Notification());

        var services = new ServiceCollection();
        services.AddScoped(_ => committeeRepo.Object);
        services.AddScoped(_ => Mock.Of<IEmailJobRepository>());
        services.AddScoped(_ => heldRepo.Object);
        services.AddScoped(_ => Mock.Of<IResidentRepository>());
        services.AddScoped(_ => Mock.Of<IUserRepository>());
        services.AddScoped(_ => Mock.Of<IDocumentFileStore>());
        services.AddScoped(_ => notifications.Object);
        var provider = services.BuildServiceProvider();

        var poller = new CommitteeMailPoller(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new EmailJobQueue(),
            Mock.Of<IGraphMailReader>(),
            new ConfigurationBuilder().Build(),
            NullLogger<CommitteeMailPoller>.Instance
        );

        // Does not throw despite the first message's raise failing.
        await poller.PollAllCommitteesAsync(CancellationToken.None);

        // The second message is still notified and stamped.
        notifications.Verify(s => s.RaiseAsync(
            It.IsAny<NotificationType>(), It.IsAny<string>(), It.IsAny<string>(),
            ok.Id.ToString("D"), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
        heldRepo.Verify(r => r.UpdateAsync(It.Is<HeldMessage>(m => m.Id == ok.Id && m.NotifiedUtc != null)), Times.Once);
    }

    [Fact]
    public async Task PollAllCommittees_leaves_still_held_message_for_later_sweep_when_stamp_conflicts()
    {
        var committee = new Committee { Id = "board", DisplayName = "Board", CommitteeEmail = "board@cohad.org", ForwardingEnabled = false };

        var committeeRepo = new Mock<ICommitteeRepository>();
        committeeRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Committee> { committee });

        var candidate = new HeldMessage
        {
            Id = Guid.NewGuid(),
            CommitteeId = "board",
            CommitteeEmail = "board@cohad.org",
            Subject = "Hello",
            Status = HeldMessageStatus.Held,
            ETag = "1",
        };

        var heldRepo = new Mock<IHeldMessageRepository>();
        heldRepo.Setup(r => r.GetAwaitingNotificationAsync(It.IsAny<DateTime>(), It.IsAny<int>()))
            .ReturnsAsync(new List<HeldMessage> { candidate });
        heldRepo.Setup(r => r.UpdateAsync(It.IsAny<HeldMessage>()))
            .ThrowsAsync(new InvalidOperationException("HeldMessage was modified by another process."));
        // The conflicting write left the message still Held (e.g. a competing sweep, not a resolve).
        heldRepo.Setup(r => r.GetByIdAsync(candidate.Id))
            .ReturnsAsync(new HeldMessage { Id = candidate.Id, CommitteeId = "board", Status = HeldMessageStatus.Held });

        var notifications = new Mock<INotificationService>();
        notifications
            .Setup(s => s.RaiseAsync(
                It.IsAny<NotificationType>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Notification());

        var services = new ServiceCollection();
        services.AddScoped(_ => committeeRepo.Object);
        services.AddScoped(_ => Mock.Of<IEmailJobRepository>());
        services.AddScoped(_ => heldRepo.Object);
        services.AddScoped(_ => Mock.Of<IResidentRepository>());
        services.AddScoped(_ => Mock.Of<IUserRepository>());
        services.AddScoped(_ => Mock.Of<IDocumentFileStore>());
        services.AddScoped(_ => notifications.Object);
        var provider = services.BuildServiceProvider();

        var poller = new CommitteeMailPoller(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new EmailJobQueue(),
            Mock.Of<IGraphMailReader>(),
            new ConfigurationBuilder().Build(),
            NullLogger<CommitteeMailPoller>.Instance
        );

        await poller.PollAllCommitteesAsync(CancellationToken.None);

        // Message still Held → do NOT resolve the (legitimate) notification; a later sweep re-stamps it.
        notifications.Verify(s => s.ResolveAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// True when the deep link targets the Approvals inbox with a parseable GUID message id — a helper
    /// (rather than an inline lambda) because Moq's expression-tree matcher can't contain a discard.
    /// </summary>
    private static bool IsApprovalsDeepLinkWithGuid(string deepLink)
    {
        const string prefix = "/manage/approvals?message=";
        return deepLink.StartsWith(prefix, StringComparison.Ordinal)
            && Guid.TryParse(deepLink.Substring(prefix.Length), out _);
    }
}
