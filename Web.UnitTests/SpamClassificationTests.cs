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

public sealed class SpamClassificationTests
{
    // ── AnthropicSpamClassifier pure mapping ────────────────────────────────

    [Fact]
    public void MapAssessment_null_is_unknown()
    {
        var result = AnthropicSpamClassifier.MapAssessment(null);
        Assert.Equal(SpamVerdict.Unknown, result.Verdict);
        Assert.Equal(SpamConfidence.Unknown, result.Confidence);
    }

    [Fact]
    public void MapAssessment_spam_high_maps_verdict_and_confidence()
    {
        var result = AnthropicSpamClassifier.MapAssessment(new AnthropicSpamClassifier.SpamAssessment
        {
            IsSpam = true,
            Confidence = "high",
            Reason = "  Cold sales outreach with a financing hook.  ",
        });

        Assert.Equal(SpamVerdict.Spam, result.Verdict);
        Assert.Equal(SpamConfidence.High, result.Confidence);
        Assert.Equal("Cold sales outreach with a financing hook.", result.Reason);
    }

    [Fact]
    public void MapAssessment_not_spam_maps_notspam()
    {
        var result = AnthropicSpamClassifier.MapAssessment(new AnthropicSpamClassifier.SpamAssessment
        {
            IsSpam = false,
            Confidence = "low",
            Reason = "Neighbor asking about a community event.",
        });

        Assert.Equal(SpamVerdict.NotSpam, result.Verdict);
        Assert.Equal(SpamConfidence.Low, result.Confidence);
    }

    [Theory]
    [InlineData("high", SpamConfidence.High)]
    [InlineData("HIGH", SpamConfidence.High)]
    [InlineData("medium", SpamConfidence.Medium)]
    [InlineData("low", SpamConfidence.Low)]
    [InlineData("", SpamConfidence.Unknown)]
    [InlineData("garbage", SpamConfidence.Unknown)]
    [InlineData(null, SpamConfidence.Unknown)]
    public void ParseConfidence_maps_known_values(string? raw, SpamConfidence expected)
    {
        Assert.Equal(expected, AnthropicSpamClassifier.ParseConfidence(raw));
    }

    // ── Poller: hold-time classification ────────────────────────────────────

    [Fact]
    public async Task Held_message_stores_spam_verdict_when_classification_enabled()
    {
        var classifier = new Mock<ISpamClassifier>();
        classifier.SetupGet(c => c.IsAvailable).Returns(true);
        classifier
            .Setup(c => c.ClassifyAsync(
                "stranger@example.com", "Stranger", "Hello", "Buy now", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpamClassificationResult
            {
                Verdict = SpamVerdict.Spam,
                Confidence = SpamConfidence.High,
                Reason = "Obvious spam",
            });

        var heldRepo = new Mock<IHeldMessageRepository>();
        heldRepo.Setup(r => r.GetByInternetMessageIdAsync("board", It.IsAny<string>())).ReturnsAsync((HeldMessage?)null);
        heldRepo.Setup(r => r.AddAsync(It.IsAny<HeldMessage>())).Returns(Task.CompletedTask);
        heldRepo.Setup(r => r.GetAwaitingNotificationAsync(It.IsAny<DateTime>(), It.IsAny<int>()))
            .ReturnsAsync(new List<HeldMessage>());

        var (poller, _) = BuildHoldScenario(classifier.Object, heldRepo, SpamConfig(enabled: true), bodyContent: "Buy now");

        await poller.PollAllCommitteesAsync(CancellationToken.None);

        heldRepo.Verify(r => r.AddAsync(It.Is<HeldMessage>(m =>
            m.SpamVerdict == SpamVerdict.Spam
            && m.SpamConfidence == SpamConfidence.High
            && m.SpamReason == "Obvious spam"
            && m.NotifiedUtc == null)), Times.Once);
    }

    [Fact]
    public async Task Held_message_survives_classifier_exception_as_unknown()
    {
        // A misbehaving classifier that throws (violating the fail-safe contract) must not abort holding
        // the message - that would leave it in the inbox to be reprocessed every poll cycle.
        var classifier = new Mock<ISpamClassifier>();
        classifier.SetupGet(c => c.IsAvailable).Returns(true);
        classifier
            .Setup(c => c.ClassifyAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var heldRepo = new Mock<IHeldMessageRepository>();
        heldRepo.Setup(r => r.GetByInternetMessageIdAsync("board", It.IsAny<string>())).ReturnsAsync((HeldMessage?)null);
        heldRepo.Setup(r => r.AddAsync(It.IsAny<HeldMessage>())).Returns(Task.CompletedTask);
        heldRepo.Setup(r => r.GetAwaitingNotificationAsync(It.IsAny<DateTime>(), It.IsAny<int>()))
            .ReturnsAsync(new List<HeldMessage>());

        var (poller, _) = BuildHoldScenario(classifier.Object, heldRepo, SpamConfig(enabled: true), bodyContent: "hi");

        await poller.PollAllCommitteesAsync(CancellationToken.None);

        heldRepo.Verify(r => r.AddAsync(It.Is<HeldMessage>(m => m.SpamVerdict == SpamVerdict.Unknown)), Times.Once);
    }

    [Fact]
    public async Task Held_message_is_not_classified_when_disabled()
    {
        var classifier = new Mock<ISpamClassifier>(MockBehavior.Strict);
        // Strict mock: any call to ClassifyAsync would throw. Disabled config must never call it.

        var heldRepo = new Mock<IHeldMessageRepository>();
        heldRepo.Setup(r => r.GetByInternetMessageIdAsync("board", It.IsAny<string>())).ReturnsAsync((HeldMessage?)null);
        heldRepo.Setup(r => r.AddAsync(It.IsAny<HeldMessage>())).Returns(Task.CompletedTask);
        heldRepo.Setup(r => r.GetAwaitingNotificationAsync(It.IsAny<DateTime>(), It.IsAny<int>()))
            .ReturnsAsync(new List<HeldMessage>());

        var (poller, _) = BuildHoldScenario(classifier.Object, heldRepo, SpamConfig(enabled: false));

        await poller.PollAllCommitteesAsync(CancellationToken.None);

        classifier.Verify(c => c.ClassifyAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        heldRepo.Verify(r => r.AddAsync(It.Is<HeldMessage>(m => m.SpamVerdict == SpamVerdict.Unknown)), Times.Once);
    }

    [Theory]
    [InlineData("<p>Hello&nbsp;world</p>", "Hello world")]
    [InlineData("<style>.x{color:red}</style><body>Buy now</body>", "Buy now")]
    [InlineData("<div>a</div>\n\n<div>b</div>", "a b")]
    public void ToPlainText_strips_markup_and_collapses_whitespace(string html, string expected)
    {
        Assert.Equal(expected, AnthropicSpamClassifier.ToPlainText(html));
    }

    [Fact]
    public void ToPlainText_drops_scripts_and_styles()
    {
        var html = "<html><head><style>.x{color:red}</style></head>"
            + "<body><p>Special financing offer</p><script>evil()</script></body></html>";

        var text = AnthropicSpamClassifier.ToPlainText(html);

        Assert.Contains("Special financing offer", text);
        Assert.DoesNotContain("color:red", text);
        Assert.DoesNotContain("evil()", text);
        Assert.DoesNotContain("<", text);
    }

    // ── Poller: sweep auto-reject ───────────────────────────────────────────

    [Fact]
    public async Task Sweep_auto_rejects_confident_spam_without_notifying()
    {
        var held = HeldSpam(SpamVerdict.Spam, SpamConfidence.High);
        var (heldRepo, notifications) = SweepMocks(held);

        var poller = BuildSweepPoller(heldRepo, notifications, SpamConfig(enabled: true, threshold: "High"));
        await poller.PollAllCommitteesAsync(CancellationToken.None);

        // Rejected by the classifier, no moderator notification, audit trail preserved.
        heldRepo.Verify(r => r.UpdateAsync(It.Is<HeldMessage>(m =>
            m.Id == held.Id
            && m.Status == HeldMessageStatus.Rejected
            && m.ReviewedByUserId == "system:spam-classifier"
            && m.ReviewedUtc != null
            && m.NotifiedUtc == null)), Times.Once);
        notifications.Verify(s => s.RaiseAsync(
            It.IsAny<NotificationType>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Sweep_notifies_when_spam_confidence_below_threshold()
    {
        var held = HeldSpam(SpamVerdict.Spam, SpamConfidence.Medium);
        var (heldRepo, notifications) = SweepMocks(held);

        var poller = BuildSweepPoller(heldRepo, notifications, SpamConfig(enabled: true, threshold: "High"));
        await poller.PollAllCommitteesAsync(CancellationToken.None);

        // Below the auto-reject bar: fall through to the normal moderator notification.
        notifications.Verify(s => s.RaiseAsync(
            NotificationType.HeldMessage, It.IsAny<string>(), It.IsAny<string>(),
            held.Id.ToString("D"), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
        heldRepo.Verify(r => r.UpdateAsync(It.Is<HeldMessage>(m => m.Status == HeldMessageStatus.Rejected)), Times.Never);
    }

    [Fact]
    public async Task Sweep_notifies_when_verdict_unknown_failsafe()
    {
        // Classification enabled but the verdict is Unknown (classifier failed at hold time). The message
        // must still reach a moderator - a classifier outage may never silently drop mail.
        var held = HeldSpam(SpamVerdict.Unknown, SpamConfidence.Unknown);
        var (heldRepo, notifications) = SweepMocks(held);

        var poller = BuildSweepPoller(heldRepo, notifications, SpamConfig(enabled: true, threshold: "High"));
        await poller.PollAllCommitteesAsync(CancellationToken.None);

        notifications.Verify(s => s.RaiseAsync(
            NotificationType.HeldMessage, It.IsAny<string>(), It.IsAny<string>(),
            held.Id.ToString("D"), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
        heldRepo.Verify(r => r.UpdateAsync(It.Is<HeldMessage>(m => m.Status == HeldMessageStatus.Rejected)), Times.Never);
    }

    [Fact]
    public async Task Sweep_falls_back_to_high_threshold_on_out_of_range_config()
    {
        // "4" parses via Enum.TryParse to an undefined SpamConfidence beyond High - before validation that
        // produced a threshold no verdict could meet, silently disabling auto-rejection. It must fall back
        // to High so High-confidence spam is still rejected.
        var held = HeldSpam(SpamVerdict.Spam, SpamConfidence.High);
        var (heldRepo, notifications) = SweepMocks(held);

        var poller = BuildSweepPoller(heldRepo, notifications, SpamConfig(enabled: true, threshold: "4"));
        await poller.PollAllCommitteesAsync(CancellationToken.None);

        heldRepo.Verify(r => r.UpdateAsync(It.Is<HeldMessage>(m => m.Status == HeldMessageStatus.Rejected)), Times.Once);
    }

    [Fact]
    public async Task Sweep_does_not_auto_reject_when_classification_disabled()
    {
        // A stored Spam/High verdict must not auto-reject once the feature is turned off - the kill-switch
        // stops auto-rejection even for already-classified records.
        var held = HeldSpam(SpamVerdict.Spam, SpamConfidence.High);
        var (heldRepo, notifications) = SweepMocks(held);

        var poller = BuildSweepPoller(heldRepo, notifications, SpamConfig(enabled: false, threshold: "High"));
        await poller.PollAllCommitteesAsync(CancellationToken.None);

        heldRepo.Verify(r => r.UpdateAsync(It.Is<HeldMessage>(m => m.Status == HeldMessageStatus.Rejected)), Times.Never);
        notifications.Verify(s => s.RaiseAsync(
            NotificationType.HeldMessage, It.IsAny<string>(), It.IsAny<string>(),
            held.Id.ToString("D"), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static IConfiguration SpamConfig(bool enabled, string threshold = "High") =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CommitteeForwarding:AntispamHoldMinutes"] = "60",
                ["CommitteeForwarding:SpamClassification:Enabled"] = enabled ? "true" : "false",
                ["CommitteeForwarding:SpamClassification:ConfidenceThreshold"] = threshold,
            })
            .Build();

    private static HeldMessage HeldSpam(SpamVerdict verdict, SpamConfidence confidence) =>
        new()
        {
            Id = Guid.NewGuid(),
            CommitteeId = "board",
            CommitteeEmail = "board@cohad.org",
            SenderEmail = "stranger@example.com",
            SenderName = "Stranger",
            Subject = "Hello",
            HeldUtc = DateTime.UtcNow.AddHours(-2),
            Status = HeldMessageStatus.Held,
            SpamVerdict = verdict,
            SpamConfidence = confidence,
            SpamReason = "test",
            ETag = "1",
        };

    private static (Mock<IHeldMessageRepository>, Mock<INotificationService>) SweepMocks(HeldMessage held)
    {
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
        return (heldRepo, notifications);
    }

    private static CommitteeMailPoller BuildSweepPoller(
        Mock<IHeldMessageRepository> heldRepo,
        Mock<INotificationService> notifications,
        IConfiguration config
    )
    {
        // Forwarding disabled: only the antispam quarantine sweep runs this cycle.
        var committee = new Committee { Id = "board", DisplayName = "Board", CommitteeEmail = "board@cohad.org", ForwardingEnabled = false };
        var committeeRepo = new Mock<ICommitteeRepository>();
        committeeRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Committee> { committee });

        var services = new ServiceCollection();
        services.AddScoped(_ => committeeRepo.Object);
        services.AddScoped(_ => Mock.Of<IEmailJobRepository>());
        services.AddScoped(_ => heldRepo.Object);
        services.AddScoped(_ => Mock.Of<IResidentRepository>());
        services.AddScoped(_ => Mock.Of<IUserRepository>());
        services.AddScoped(_ => Mock.Of<IDocumentFileStore>());
        services.AddScoped(_ => notifications.Object);
        var provider = services.BuildServiceProvider();

        return new CommitteeMailPoller(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new EmailJobQueue(),
            Mock.Of<IGraphMailReader>(),
            new DisabledSpamClassifier(),
            config,
            NullLogger<CommitteeMailPoller>.Instance
        );
    }

    private static (CommitteeMailPoller, Mock<INotificationService>) BuildHoldScenario(
        ISpamClassifier classifier,
        Mock<IHeldMessageRepository> heldRepo,
        IConfiguration config,
        string? bodyContent = null
    )
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

        var residentRepo = new Mock<IResidentRepository>();
        residentRepo.Setup(r => r.GetByIdsAsync(It.IsAny<IEnumerable<Guid>>())).ReturnsAsync(new List<Resident>());
        residentRepo.Setup(r => r.GetByEmailAsync(It.IsAny<string>())).ReturnsAsync(new List<Resident>()); // unknown → hold

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
            Body = bodyContent == null ? null : new ItemBody { ContentType = BodyType.Text, Content = bodyContent },
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
            classifier,
            config,
            NullLogger<CommitteeMailPoller>.Instance
        );

        return (poller, notifications);
    }
}
