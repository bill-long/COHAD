using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Graph.Models;
using Moq;
using Web.MockData;
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
        services.AddScoped<IEmailSuppressionRepository>(_ => new MockEmailSuppressionRepository());
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
            new DisabledSpamClassifier(),
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
    public async Task PollAllCommittees_classifies_after_persisting_held_record_then_stores_verdict()
    {
        var order = new List<string>();
        // Seed to a non-default so asserting Unknown is meaningful (proves Add ran and captured add-time state).
        var verdictAtAddTime = SpamVerdict.Spam;

        var (poller, heldRepo, classifier, _) = BuildSpamHoldScenario(held =>
        {
            held.Setup(r => r.AddAsync(It.IsAny<HeldMessage>()))
                .Callback<HeldMessage>(m => { order.Add("add"); verdictAtAddTime = m.SpamVerdict; })
                .Returns(Task.CompletedTask);
            held.Setup(r => r.UpdateAsync(It.IsAny<HeldMessage>()))
                .Callback(() => order.Add("update"))
                .Returns(Task.CompletedTask);
        });
        classifier
            .Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("classify"))
            .ReturnsAsync(new SpamClassificationResult { Verdict = SpamVerdict.Spam, Confidence = SpamConfidence.High, Reason = "cold outreach" });

        await poller.PollAllCommitteesAsync(CancellationToken.None);

        // The paid classifier runs only after the held record is durably persisted: the record is added
        // with the default Unknown verdict, then classified, then updated with the resulting verdict.
        Assert.Equal(new[] { "add", "classify", "update" }, order);
        Assert.Equal(SpamVerdict.Unknown, verdictAtAddTime);
        classifier.Verify(
            c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
        heldRepo.Verify(
            r => r.UpdateAsync(It.Is<HeldMessage>(m =>
                m.SpamVerdict == SpamVerdict.Spam && m.SpamConfidence == SpamConfidence.High && m.SpamReason == "cold outreach")),
            Times.Once);
    }

    [Fact]
    public async Task PollAllCommittees_does_not_run_classifier_when_held_record_persist_fails()
    {
        var (poller, heldRepo, classifier, _) = BuildSpamHoldScenario(held =>
        {
            // Transient, non-409 Cosmos write failure: the message is left in the inbox to be reprocessed.
            held.Setup(r => r.AddAsync(It.IsAny<HeldMessage>()))
                .ThrowsAsync(new CosmosException("service unavailable", HttpStatusCode.ServiceUnavailable, 0, "activity", 0));
        });
        classifier
            .Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpamClassificationResult());

        await poller.PollAllCommitteesAsync(CancellationToken.None);

        // The paid classifier must not run before the record is durably persisted; otherwise a transient
        // write failure would re-bill classification on every poll cycle until the write eventually succeeds.
        classifier.Verify(
            c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        heldRepo.Verify(r => r.UpdateAsync(It.IsAny<HeldMessage>()), Times.Never);
    }

    [Fact]
    public async Task PollAllCommittees_does_not_second_write_when_classifier_unavailable()
    {
        // SpamClassification enabled but no real classifier (DisabledSpamClassifier: IsAvailable == false).
        var (poller, heldRepo, classifier, _) = BuildSpamHoldScenario(held =>
        {
            held.Setup(r => r.AddAsync(It.IsAny<HeldMessage>())).Returns(Task.CompletedTask);
        });
        classifier.Setup(c => c.IsAvailable).Returns(false);

        await poller.PollAllCommitteesAsync(CancellationToken.None);

        // The record is added once; with no usable classifier there is no verdict to store, so the redundant
        // second Cosmos write (and the no-op classify call) must be skipped entirely.
        heldRepo.Verify(r => r.AddAsync(It.IsAny<HeldMessage>()), Times.Once);
        heldRepo.Verify(r => r.UpdateAsync(It.IsAny<HeldMessage>()), Times.Never);
        classifier.Verify(
            c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PollAllCommittees_still_processes_message_when_verdict_store_fails_transiently()
    {
        // AddAsync succeeds (record is held) but the follow-up verdict UpdateAsync hits a transient Cosmos
        // error that is NOT a 412 concurrency conflict.
        var (poller, heldRepo, classifier, graph) = BuildSpamHoldScenario(held =>
        {
            held.Setup(r => r.AddAsync(It.IsAny<HeldMessage>())).Returns(Task.CompletedTask);
            held.Setup(r => r.UpdateAsync(It.IsAny<HeldMessage>()))
                .ThrowsAsync(new CosmosException("throttled", HttpStatusCode.TooManyRequests, 0, "activity", 0));
        });
        classifier
            .Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpamClassificationResult { Verdict = SpamVerdict.Spam, Confidence = SpamConfidence.High });

        // Must not throw: storing the verdict is best-effort and must not disrupt the hold flow.
        await poller.PollAllCommitteesAsync(CancellationToken.None);

        // The record was held and the verdict update attempted; despite the transient failure the message is
        // still moved to the Processed folder (the invariant the single-write hold path always upheld).
        heldRepo.Verify(r => r.UpdateAsync(It.IsAny<HeldMessage>()), Times.Once);
        graph.Verify(g => g.MoveMessageAsync("board@cohad.org", "graph-1", "processed-folder", It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Builds a poll scenario with one unknown-sender message to a forwarding committee and spam
    /// classification enabled, returning the poller plus the held-message and classifier mocks so a test
    /// can configure persistence behavior and assert on classification. Mirrors the standard hold setup.
    /// </summary>
    private static (CommitteeMailPoller poller, Mock<IHeldMessageRepository> heldRepo, Mock<ISpamClassifier> classifier, Mock<IGraphMailReader> graph)
        BuildSpamHoldScenario(Action<Mock<IHeldMessageRepository>> configureHeld)
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
        heldRepo.Setup(r => r.GetAwaitingNotificationAsync(It.IsAny<DateTime>(), It.IsAny<int>()))
            .ReturnsAsync(new List<HeldMessage>());
        configureHeld(heldRepo);

        var residentRepo = new Mock<IResidentRepository>();
        residentRepo.Setup(r => r.GetByIdsAsync(It.IsAny<IEnumerable<Guid>>())).ReturnsAsync(new List<Resident>());
        residentRepo.Setup(r => r.GetByEmailAsync(It.IsAny<string>())).ReturnsAsync(new List<Resident>()); // unknown sender → hold

        var services = new ServiceCollection();
        services.AddScoped(_ => committeeRepo.Object);
        services.AddScoped(_ => emailJobRepo.Object);
        services.AddScoped(_ => heldRepo.Object);
        services.AddScoped(_ => residentRepo.Object);
        services.AddScoped(_ => Mock.Of<IUserRepository>());
        services.AddScoped(_ => Mock.Of<IDocumentFileStore>());
        services.AddScoped<IEmailSuppressionRepository>(_ => new MockEmailSuppressionRepository());
        services.AddScoped(_ => Mock.Of<INotificationService>());
        var provider = services.BuildServiceProvider();

        var message = new Message
        {
            Id = "graph-1",
            InternetMessageId = "<msg-1@example.com>",
            Subject = "Buy now",
            ReceivedDateTime = DateTimeOffset.UtcNow,
            From = new Recipient { EmailAddress = new Microsoft.Graph.Models.EmailAddress { Address = "stranger@example.com", Name = "Stranger" } },
        };

        var graphReader = new Mock<IGraphMailReader>();
        graphReader.Setup(g => g.GetInboxMessagesAsync("board@cohad.org", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Message> { message });
        graphReader.Setup(g => g.GetOrCreateFolderAsync("board@cohad.org", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("processed-folder");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["CommitteeForwarding:SpamClassification:Enabled"] = "true" })
            .Build();

        var classifier = new Mock<ISpamClassifier>();
        classifier.Setup(c => c.IsAvailable).Returns(true); // a real classifier is configured by default

        var poller = new CommitteeMailPoller(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new EmailJobQueue(),
            graphReader.Object,
            classifier.Object,
            config,
            NullLogger<CommitteeMailPoller>.Instance
        );

        return (poller, heldRepo, classifier, graphReader);
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
        services.AddScoped<IEmailSuppressionRepository>(_ => new MockEmailSuppressionRepository());
        services.AddScoped(_ => notifications.Object);
        var provider = services.BuildServiceProvider();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["CommitteeForwarding:AntispamHoldMinutes"] = "60" })
            .Build();
        var poller = new CommitteeMailPoller(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new EmailJobQueue(),
            Mock.Of<IGraphMailReader>(),
            new DisabledSpamClassifier(),
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
        services.AddScoped<IEmailSuppressionRepository>(_ => new MockEmailSuppressionRepository());
        services.AddScoped(_ => notifications.Object);
        var provider = services.BuildServiceProvider();

        var poller = new CommitteeMailPoller(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new EmailJobQueue(),
            Mock.Of<IGraphMailReader>(),
            new DisabledSpamClassifier(),
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
        services.AddScoped<IEmailSuppressionRepository>(_ => new MockEmailSuppressionRepository());
        services.AddScoped(_ => notifications.Object);
        var provider = services.BuildServiceProvider();

        var poller = new CommitteeMailPoller(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new EmailJobQueue(),
            Mock.Of<IGraphMailReader>(),
            new DisabledSpamClassifier(),
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
        services.AddScoped<IEmailSuppressionRepository>(_ => new MockEmailSuppressionRepository());
        services.AddScoped(_ => notifications.Object);
        var provider = services.BuildServiceProvider();

        var poller = new CommitteeMailPoller(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new EmailJobQueue(),
            Mock.Of<IGraphMailReader>(),
            new DisabledSpamClassifier(),
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
        services.AddScoped<IEmailSuppressionRepository>(_ => new MockEmailSuppressionRepository());
        services.AddScoped(_ => notifications.Object);
        var provider = services.BuildServiceProvider();

        var poller = new CommitteeMailPoller(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new EmailJobQueue(),
            Mock.Of<IGraphMailReader>(),
            new DisabledSpamClassifier(),
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
        services.AddScoped<IEmailSuppressionRepository>(_ => new MockEmailSuppressionRepository());
        services.AddScoped(_ => notifications.Object);
        var provider = services.BuildServiceProvider();

        var poller = new CommitteeMailPoller(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new EmailJobQueue(),
            Mock.Of<IGraphMailReader>(),
            new DisabledSpamClassifier(),
            new ConfigurationBuilder().Build(),
            NullLogger<CommitteeMailPoller>.Instance
        );

        await poller.PollAllCommitteesAsync(CancellationToken.None);

        // Message still Held → do NOT resolve the (legitimate) notification; a later sweep re-stamps it.
        notifications.Verify(s => s.ResolveAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PollAllCommittees_forwarded_job_records_the_resident_as_the_author_not_the_committee()
    {
        var memberResidentId = Guid.NewGuid();
        var committee = new Committee
        {
            Id = "architectural",
            DisplayName = "Architectural Committee",
            CommitteeEmail = "architectural@cohad.org",
            ForwardingEnabled = true,
            ForwardingSenderFilter = ForwardingSenderFilter.DirectoryOnly,
            Members = new List<CommitteeMember>
            {
                new CommitteeMember { ResidentId = memberResidentId, ReceivesForwardedEmail = true },
            },
        };

        var committeeRepo = new Mock<ICommitteeRepository>();
        committeeRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Committee> { committee });
        committeeRepo.Setup(r => r.GetByIdAsync("architectural")).ReturnsAsync(committee);
        committeeRepo.Setup(r => r.UpsertAsync(It.IsAny<Committee>())).ReturnsAsync((Committee c) => c);

        var emailJobRepo = new Mock<IEmailJobRepository>();
        emailJobRepo.Setup(r => r.GetByInternetMessageIdAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync((EmailJob?)null);
        EmailJob? created = null;
        emailJobRepo.Setup(r => r.AddAsync(It.IsAny<EmailJob>()))
            .Callback<EmailJob>(j => created = j)
            .Returns(Task.CompletedTask);

        var heldRepo = new Mock<IHeldMessageRepository>();
        heldRepo.Setup(r => r.GetByInternetMessageIdAsync("architectural", It.IsAny<string>())).ReturnsAsync((HeldMessage?)null);
        heldRepo.Setup(r => r.GetAwaitingNotificationAsync(It.IsAny<DateTime>(), It.IsAny<int>()))
            .ReturnsAsync(new List<HeldMessage>());

        // The sender is in the directory, so the message forwards rather than being held.
        var sender = new Resident
        {
            Id = Guid.NewGuid(),
            HomeId = Guid.NewGuid(),
            EmailAddresses = new List<Web.Models.EmailAddress> { new() { Address = "jane@example.com" } },
        };
        var member = new Resident
        {
            Id = memberResidentId,
            HomeId = Guid.NewGuid(),
            EmailAddresses = new List<Web.Models.EmailAddress> { new() { Address = "member@example.com" } },
        };

        var residentRepo = new Mock<IResidentRepository>();
        residentRepo.Setup(r => r.GetByEmailAsync("jane@example.com")).ReturnsAsync(new List<Resident> { sender });
        residentRepo.Setup(r => r.GetByIdsAsync(It.IsAny<IEnumerable<Guid>>())).ReturnsAsync(new List<Resident> { member });

        var services = new ServiceCollection();
        services.AddScoped(_ => committeeRepo.Object);
        services.AddScoped(_ => emailJobRepo.Object);
        services.AddScoped(_ => heldRepo.Object);
        services.AddScoped(_ => residentRepo.Object);
        services.AddScoped(_ => Mock.Of<IUserRepository>());
        services.AddScoped(_ => Mock.Of<IDocumentFileStore>());
        services.AddScoped<IEmailSuppressionRepository>(_ => new MockEmailSuppressionRepository());
        services.AddScoped(_ => Mock.Of<INotificationService>());
        var provider = services.BuildServiceProvider();

        var message = new Message
        {
            Id = "graph-1",
            InternetMessageId = "<msg-1@example.com>",
            Subject = "Request to repaint front door",
            ReceivedDateTime = DateTimeOffset.UtcNow,
            From = new Recipient { EmailAddress = new Microsoft.Graph.Models.EmailAddress { Address = "jane@example.com", Name = "Jane Doe" } },
        };

        var graphReader = new Mock<IGraphMailReader>();
        graphReader.Setup(g => g.GetInboxMessagesAsync("architectural@cohad.org", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Message> { message });
        graphReader.Setup(g => g.GetOrCreateFolderAsync("architectural@cohad.org", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("processed-folder");

        var poller = new CommitteeMailPoller(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new EmailJobQueue(),
            graphReader.Object,
            new DisabledSpamClassifier(),
            new ConfigurationBuilder().Build(),
            NullLogger<CommitteeMailPoller>.Instance
        );

        await poller.PollAllCommitteesAsync(CancellationToken.None);

        Assert.NotNull(created);
        // Sent as the committee mailbox (also the forwarding idempotency key)...
        Assert.Equal("architectural@cohad.org", created!.FromEmail);
        // ...but written by the resident, and shown as such.
        Assert.Equal("jane@example.com", created.OriginalSenderEmail);
        Assert.Equal("Jane Doe", created.OriginalSenderDisplay);
        Assert.Equal("jane@example.com", created.ReplyToEmail);
        Assert.Equal("Architectural Committee forwarding members", created.ToDisplay);
    }

    [Fact]
    public async Task PollAllCommittees_forwards_to_second_address_when_first_is_suppressed()
    {
        var memberResidentId = Guid.NewGuid();
        var member = new Resident
        {
            Id = memberResidentId,
            HomeId = Guid.NewGuid(),
            EmailAddresses = new List<Web.Models.EmailAddress>
            {
                new() { Address = "bounced@example.com" },
                new() { Address = "backup@example.com" },
            },
        };

        var (poller, emailJobRepo, _, _) = BuildForwardScenario(
            member,
            activeSuppressions: new[] { "bounced@example.com" });

        EmailJob? created = null;
        emailJobRepo.Setup(r => r.AddAsync(It.IsAny<EmailJob>()))
            .Callback<EmailJob>(j => created = j)
            .Returns(Task.CompletedTask);

        await poller.PollAllCommitteesAsync(CancellationToken.None);

        Assert.NotNull(created);
        var recipient = Assert.Single(created!.Recipients);
        Assert.Equal("backup@example.com", recipient.Email);
    }

    [Fact]
    public async Task PollAllCommittees_when_every_member_is_suppressed_moves_to_processed_without_a_job()
    {
        var memberResidentId = Guid.NewGuid();
        var member = new Resident
        {
            Id = memberResidentId,
            HomeId = Guid.NewGuid(),
            EmailAddresses = new List<Web.Models.EmailAddress> { new() { Address = "bounced@example.com" } },
        };

        var (poller, emailJobRepo, _, graph) = BuildForwardScenario(
            member,
            activeSuppressions: new[] { "bounced@example.com" });

        await poller.PollAllCommitteesAsync(CancellationToken.None);

        // No deliverable recipients remain → no job created; the message still leaves the inbox
        // (the existing empty-result handling that predates the suppression list).
        emailJobRepo.Verify(r => r.AddAsync(It.IsAny<EmailJob>()), Times.Never);
        graph.Verify(g => g.MoveMessageAsync("board@cohad.org", "graph-1", "processed-folder", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PollAllCommittees_when_suppression_read_throws_defers_forwarding_but_still_polls()
    {
        // A throwing suppression read must NOT abort the whole cycle: the inbox is still polled
        // (holding/quarantine is independent of the suppression store), but a directory-sender
        // message that would forward is deferred - left in the inbox, no job created - because
        // forwarding with an unknown suppression state would risk mailing a suppressed address.
        var memberResidentId = Guid.NewGuid();
        var committee = new Committee
        {
            Id = "board",
            DisplayName = "Board",
            CommitteeEmail = "board@cohad.org",
            ForwardingEnabled = true,
            ForwardingSenderFilter = ForwardingSenderFilter.DirectoryOnly,
            Members = new List<CommitteeMember> { new() { ResidentId = memberResidentId, ReceivesForwardedEmail = true } },
        };

        var committeeRepo = new Mock<ICommitteeRepository>();
        committeeRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Committee> { committee });
        committeeRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);
        (string? Status, string? Error) stamped = (null, null);
        committeeRepo.Setup(r => r.UpsertAsync(It.IsAny<Committee>()))
            .Callback<Committee>(c => stamped = (c.LastPollStatus, c.LastPollError))
            .ReturnsAsync((Committee c) => c);

        var emailJobRepo = new Mock<IEmailJobRepository>();
        emailJobRepo.Setup(r => r.GetByInternetMessageIdAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync((EmailJob?)null);

        var heldRepo = new Mock<IHeldMessageRepository>();
        heldRepo.Setup(r => r.GetByInternetMessageIdAsync("board", It.IsAny<string>())).ReturnsAsync((HeldMessage?)null);
        heldRepo.Setup(r => r.GetAwaitingNotificationAsync(It.IsAny<DateTime>(), It.IsAny<int>()))
            .ReturnsAsync(new List<HeldMessage>());

        // Sender is in the directory, so the message would forward (not be held).
        var sender = new Resident
        {
            Id = Guid.NewGuid(),
            HomeId = Guid.NewGuid(),
            EmailAddresses = new List<Web.Models.EmailAddress> { new() { Address = "jane@example.com" } },
        };
        var member = new Resident
        {
            Id = memberResidentId,
            HomeId = Guid.NewGuid(),
            EmailAddresses = new List<Web.Models.EmailAddress> { new() { Address = "member@example.com" } },
        };
        var residentRepo = new Mock<IResidentRepository>();
        residentRepo.Setup(r => r.GetByEmailAsync("jane@example.com")).ReturnsAsync(new List<Resident> { sender });
        residentRepo.Setup(r => r.GetByIdsAsync(It.IsAny<IEnumerable<Guid>>())).ReturnsAsync(new List<Resident> { member });

        var suppressionRepo = new Mock<IEmailSuppressionRepository>();
        suppressionRepo.Setup(r => r.GetActiveAsync())
            .ThrowsAsync(new InvalidOperationException("Cosmos container 'EmailSuppression' not found."));

        var services = new ServiceCollection();
        services.AddScoped(_ => committeeRepo.Object);
        services.AddScoped(_ => emailJobRepo.Object);
        services.AddScoped(_ => heldRepo.Object);
        services.AddScoped(_ => residentRepo.Object);
        services.AddScoped(_ => Mock.Of<IUserRepository>());
        services.AddScoped(_ => Mock.Of<IDocumentFileStore>());
        services.AddScoped(_ => suppressionRepo.Object);
        services.AddScoped(_ => Mock.Of<INotificationService>());
        var provider = services.BuildServiceProvider();

        var message = new Message
        {
            Id = "graph-1",
            InternetMessageId = "<msg-1@example.com>",
            Subject = "Hello",
            ReceivedDateTime = DateTimeOffset.UtcNow,
            From = new Recipient { EmailAddress = new Microsoft.Graph.Models.EmailAddress { Address = "jane@example.com", Name = "Jane Doe" } },
        };
        var graph = new Mock<IGraphMailReader>();
        graph.Setup(g => g.GetInboxMessagesAsync("board@cohad.org", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Message> { message });
        graph.Setup(g => g.GetOrCreateFolderAsync("board@cohad.org", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("processed-folder");

        var poller = new CommitteeMailPoller(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new EmailJobQueue(),
            graph.Object,
            new DisabledSpamClassifier(),
            new ConfigurationBuilder().Build(),
            NullLogger<CommitteeMailPoller>.Instance
        );

        await poller.PollAllCommitteesAsync(CancellationToken.None);

        // The cycle still polled the inbox (holding/cleanup unaffected)...
        graph.Verify(g => g.GetInboxMessagesAsync("board@cohad.org", It.IsAny<CancellationToken>()), Times.Once);
        // ...but forwarding was deferred: no job, and the message is left in the inbox (not moved to
        // Processed) so it retries next cycle.
        emailJobRepo.Verify(r => r.AddAsync(It.IsAny<EmailJob>()), Times.Never);
        graph.Verify(g => g.MoveMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        // ...and the deferral is surfaced on the committee's poll status, not a bare Success.
        Assert.Equal("Forwarding deferred", stamped.Status);
        Assert.Contains("Suppression list unavailable", stamped.Error);
    }

    [Fact]
    public async Task PollAllCommittees_still_holds_unknown_sender_when_suppression_read_throws()
    {
        // The safety property: held-message quarantine of a non-directory sender does not depend on
        // the suppression store, so a throwing suppression read must not stop new mail from being
        // held.
        var committee = new Committee
        {
            Id = "board",
            DisplayName = "Board",
            CommitteeEmail = "board@cohad.org",
            ForwardingEnabled = true,
            ForwardingSenderFilter = ForwardingSenderFilter.DirectoryOnly,
            Members = new List<CommitteeMember> { new() { ResidentId = Guid.NewGuid(), ReceivesForwardedEmail = true } },
        };

        var committeeRepo = new Mock<ICommitteeRepository>();
        committeeRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Committee> { committee });
        committeeRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);
        (string? Status, string? Error) stamped = (null, null);
        committeeRepo.Setup(r => r.UpsertAsync(It.IsAny<Committee>()))
            .Callback<Committee>(c => stamped = (c.LastPollStatus, c.LastPollError))
            .ReturnsAsync((Committee c) => c);

        var emailJobRepo = new Mock<IEmailJobRepository>();
        emailJobRepo.Setup(r => r.GetByInternetMessageIdAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync((EmailJob?)null);

        var heldRepo = new Mock<IHeldMessageRepository>();
        heldRepo.Setup(r => r.GetByInternetMessageIdAsync("board", It.IsAny<string>())).ReturnsAsync((HeldMessage?)null);
        heldRepo.Setup(r => r.AddAsync(It.IsAny<HeldMessage>())).Returns(Task.CompletedTask);
        heldRepo.Setup(r => r.GetAwaitingNotificationAsync(It.IsAny<DateTime>(), It.IsAny<int>()))
            .ReturnsAsync(new List<HeldMessage>());

        var residentRepo = new Mock<IResidentRepository>();
        residentRepo.Setup(r => r.GetByIdsAsync(It.IsAny<IEnumerable<Guid>>())).ReturnsAsync(new List<Resident>());
        residentRepo.Setup(r => r.GetByEmailAsync(It.IsAny<string>())).ReturnsAsync(new List<Resident>()); // unknown sender → hold

        var suppressionRepo = new Mock<IEmailSuppressionRepository>();
        suppressionRepo.Setup(r => r.GetActiveAsync())
            .ThrowsAsync(new InvalidOperationException("Cosmos container 'EmailSuppression' not found."));

        var services = new ServiceCollection();
        services.AddScoped(_ => committeeRepo.Object);
        services.AddScoped(_ => emailJobRepo.Object);
        services.AddScoped(_ => heldRepo.Object);
        services.AddScoped(_ => residentRepo.Object);
        services.AddScoped(_ => Mock.Of<IUserRepository>());
        services.AddScoped(_ => Mock.Of<IDocumentFileStore>());
        services.AddScoped(_ => suppressionRepo.Object);
        services.AddScoped(_ => Mock.Of<INotificationService>());
        var provider = services.BuildServiceProvider();

        var message = new Message
        {
            Id = "graph-1",
            InternetMessageId = "<msg-1@example.com>",
            Subject = "Buy now",
            ReceivedDateTime = DateTimeOffset.UtcNow,
            From = new Recipient { EmailAddress = new Microsoft.Graph.Models.EmailAddress { Address = "stranger@example.com", Name = "Stranger" } },
        };
        var graph = new Mock<IGraphMailReader>();
        graph.Setup(g => g.GetInboxMessagesAsync("board@cohad.org", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Message> { message });
        graph.Setup(g => g.GetOrCreateFolderAsync("board@cohad.org", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("processed-folder");

        var poller = new CommitteeMailPoller(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new EmailJobQueue(),
            graph.Object,
            new DisabledSpamClassifier(),
            new ConfigurationBuilder().Build(),
            NullLogger<CommitteeMailPoller>.Instance
        );

        await poller.PollAllCommitteesAsync(CancellationToken.None);

        // The unknown-sender message is still held despite the suppression store being unreadable.
        heldRepo.Verify(r => r.AddAsync(It.Is<HeldMessage>(m => m.NotifiedUtc == null)), Times.Once);
        // Nothing was actually deferred (the message was held, not forwarded), so the poll status is
        // not overstated as 'Forwarding deferred'.
        Assert.Equal("Success", stamped.Status);
        Assert.Null(stamped.Error);
    }

    /// <summary>
    /// Builds a poll scenario with one directory-sender message forwarding to a single committee
    /// member, with the given addresses active on the suppression list. Returns the poller plus the
    /// email-job, notification-service, and graph mocks for per-test configuration and asserts.
    /// </summary>
    private static (CommitteeMailPoller poller, Mock<IEmailJobRepository> emailJobRepo, Mock<INotificationService> notifications, Mock<IGraphMailReader> graph)
        BuildForwardScenario(Resident member, IReadOnlyList<string> activeSuppressions)
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
                new CommitteeMember { ResidentId = member.Id, ReceivesForwardedEmail = true },
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
        heldRepo.Setup(r => r.GetAwaitingNotificationAsync(It.IsAny<DateTime>(), It.IsAny<int>()))
            .ReturnsAsync(new List<HeldMessage>());

        // The sender is in the directory, so the message forwards rather than being held.
        var sender = new Resident
        {
            Id = Guid.NewGuid(),
            HomeId = Guid.NewGuid(),
            EmailAddresses = new List<Web.Models.EmailAddress> { new() { Address = "jane@example.com" } },
        };
        var residentRepo = new Mock<IResidentRepository>();
        residentRepo.Setup(r => r.GetByEmailAsync("jane@example.com")).ReturnsAsync(new List<Resident> { sender });
        residentRepo.Setup(r => r.GetByIdsAsync(It.IsAny<IEnumerable<Guid>>())).ReturnsAsync(new List<Resident> { member });

        var suppressionRepo = new Mock<IEmailSuppressionRepository>();
        suppressionRepo.Setup(r => r.GetActiveAsync()).ReturnsAsync(
            activeSuppressions.Select(a => new EmailSuppression
            {
                Id = EmailSuppression.MakeId(a),
                Email = EmailSuppression.NormalizeAddress(a),
                Reason = SuppressionReason.HardBounce,
            }).ToList());

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
        services.AddScoped(_ => emailJobRepo.Object);
        services.AddScoped(_ => heldRepo.Object);
        services.AddScoped(_ => residentRepo.Object);
        services.AddScoped(_ => Mock.Of<IUserRepository>());
        services.AddScoped(_ => Mock.Of<IDocumentFileStore>());
        services.AddScoped(_ => suppressionRepo.Object);
        services.AddScoped(_ => notifications.Object);
        var provider = services.BuildServiceProvider();

        var message = new Message
        {
            Id = "graph-1",
            InternetMessageId = "<msg-1@example.com>",
            Subject = "Hello",
            ReceivedDateTime = DateTimeOffset.UtcNow,
            From = new Recipient { EmailAddress = new Microsoft.Graph.Models.EmailAddress { Address = "jane@example.com", Name = "Jane Doe" } },
        };

        var graphReader = new Mock<IGraphMailReader>();
        graphReader.Setup(g => g.GetInboxMessagesAsync("board@cohad.org", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Message> { message });
        graphReader.Setup(g => g.GetOrCreateFolderAsync("board@cohad.org", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("processed-folder");

        var poller = new CommitteeMailPoller(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new EmailJobQueue(),
            graphReader.Object,
            new DisabledSpamClassifier(),
            new ConfigurationBuilder().Build(),
            NullLogger<CommitteeMailPoller>.Instance
        );

        return (poller, emailJobRepo, notifications, graphReader);
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
