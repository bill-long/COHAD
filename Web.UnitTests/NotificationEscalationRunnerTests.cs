using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Web.MockData;
using Web.Models;
using Web.Services;
using Web.Services.Repositories;
using Xunit;

namespace Web.UnitTests;

public sealed class NotificationEscalationRunnerTests
{
    /// <summary>Captures emitted log entries so tests can assert observability warnings fire.</summary>
    private sealed class ListLogger<T> : ILogger<T>
    {
        public readonly List<(LogLevel Level, string Message)> Entries = new();

        // Reuse the BCL's no-op scope rather than hand-rolling one; nothing under test uses scopes.
        IDisposable? ILogger.BeginScope<TState>(TState state) => NullLogger.Instance.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class Harness
    {
        public readonly MockNotificationRepository Notifications = new();
        public readonly MockNotificationDigestStateRepository DigestState = new();
        public readonly Mock<INotificationRecipientResolver> Resolver = new();
        public readonly Mock<ICommitteeRepository> Committees = new();
        public readonly Mock<IEmailJobRepository> EmailJobs = new();
        public readonly Mock<IDocumentFileStore> FileStore = new();
        public readonly EmailJobQueue Queue = new();
        public readonly List<EmailJob> CapturedJobs = new();
        public string? UploadedHtml;

        /// <summary>Every uploaded digest body, keyed by its blob path (one per recipient/job).</summary>
        public readonly Dictionary<string, string> UploadedByPath = new();

        /// <summary>When set, digest items are rendered as deep links under this base URL.</summary>
        public string? AppBaseUrl;

        public readonly ListLogger<NotificationEscalationRunner> Logger = new();

        public readonly NotificationEscalationOptions Options = new()
        {
            Enabled = true,
            SweepIntervalMinutes = 15,
            GracePeriodMinutes = 30,
            MinDigestIntervalHours = 6,
            MaxItemsPerDigest = 10,
        };

        public Harness()
        {
            Committees.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Committee>());
            EmailJobs
                .Setup(r => r.AddAsync(It.IsAny<EmailJob>()))
                .Callback<EmailJob>(j => CapturedJobs.Add(j))
                .Returns(Task.CompletedTask);
            FileStore
                .Setup(f => f.UploadAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>()))
                .Callback<string, Stream, string>(
                    (path, stream, _) =>
                    {
                        using var sr = new StreamReader(stream, leaveOpen: true);
                        var html = sr.ReadToEnd();
                        UploadedHtml = html;
                        UploadedByPath[path] = html;
                    }
                )
                .Returns(Task.CompletedTask);
            // Default: no recipients for any audience; tests opt in per audience.
            Resolver
                .Setup(r => r.ResolveAudienceEmailsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<string>());
        }

        public void RecipientsFor(string audience, params string[] emails)
        {
            Resolver
                .Setup(r => r.ResolveAudienceEmailsAsync(audience, It.IsAny<CancellationToken>()))
                .ReturnsAsync(emails);
        }

        public NotificationEscalationRunner Build() =>
            new(
                Notifications,
                Resolver.Object,
                Committees.Object,
                DigestState,
                EmailJobs.Object,
                FileStore.Object,
                Queue,
                Microsoft.Extensions.Options.Options.Create(Options),
                BuildConfig(AppBaseUrl),
                Logger
            );
    }

    private static IConfiguration BuildConfig(string? appBaseUrl) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AppBaseUrl"] = appBaseUrl })
            .Build();

    private static async Task<Notification> AddNotificationAsync(
        MockNotificationRepository repo,
        string audience,
        DateTime createdUtc,
        NotificationType type = NotificationType.Registration,
        DateTime? escalatedUtc = null,
        string? targetId = null
    )
    {
        targetId ??= Guid.NewGuid().ToString();
        var notification = new Notification
        {
            Id = Notification.DeterministicId(NotificationTargetType.User, targetId),
            Type = type,
            AudienceKey = audience,
            TargetType = NotificationTargetType.User,
            TargetId = targetId,
            Title = "New user registered",
            Summary = "Jane Doe — 123 Mock Lane",
            CreatedUtc = createdUtc,
            EscalatedUtc = escalatedUtc,
        };
        await repo.AddAsync(notification);
        return notification;
    }

    [Fact]
    public async Task Escalates_aged_notification_to_due_recipient()
    {
        var h = new Harness();
        h.RecipientsFor(NotificationAudience.Administrators, "admin@example.com");
        var n = await AddNotificationAsync(
            h.Notifications,
            NotificationAudience.Administrators,
            DateTime.UtcNow.AddHours(-2)
        );

        await h.Build().RunOnceAsync(CancellationToken.None);

        var job = Assert.Single(h.CapturedJobs);
        Assert.Equal("notification-escalation", job.Category);
        Assert.True(job.GroupRecipients);
        var recipient = Assert.Single(job.Recipients);
        Assert.Equal("admin@example.com", recipient.Email);
        Assert.Equal(Guid.Empty, recipient.HomeId);

        // The notification is stamped escalated with the job id.
        var stamped = await h.Notifications.GetByIdAsync(n.Id);
        Assert.NotNull(stamped!.EscalatedUtc);
        Assert.Equal(job.Id, stamped.EscalationJobId);

        // The recipient's digest state is recorded.
        var state = await h.DigestState.GetAsync("admin@example.com");
        Assert.NotNull(state);

        // The job was enqueued for the processor.
        var dequeued = await h.Queue.DequeueAsync(new CancellationTokenSource(1000).Token);
        Assert.Equal(job.Id, dequeued);
    }

    [Fact]
    public async Task Skips_notification_within_grace_period()
    {
        var h = new Harness();
        h.RecipientsFor(NotificationAudience.Administrators, "admin@example.com");
        var n = await AddNotificationAsync(
            h.Notifications,
            NotificationAudience.Administrators,
            DateTime.UtcNow.AddMinutes(-5)
        );

        await h.Build().RunOnceAsync(CancellationToken.None);

        Assert.Empty(h.CapturedJobs);
        var fresh = await h.Notifications.GetByIdAsync(n.Id);
        Assert.Null(fresh!.EscalatedUtc);
    }

    [Fact]
    public async Task Skips_already_escalated_notification()
    {
        var h = new Harness();
        h.RecipientsFor(NotificationAudience.Administrators, "admin@example.com");
        await AddNotificationAsync(
            h.Notifications,
            NotificationAudience.Administrators,
            DateTime.UtcNow.AddHours(-2),
            escalatedUtc: DateTime.UtcNow.AddHours(-1)
        );

        await h.Build().RunOnceAsync(CancellationToken.None);

        Assert.Empty(h.CapturedJobs);
    }

    [Fact]
    public async Task Throttle_skips_recently_digested_recipient_and_leaves_unescalated()
    {
        var h = new Harness();
        h.RecipientsFor(NotificationAudience.Administrators, "admin@example.com");
        await h.DigestState.UpsertAsync(
            new NotificationDigestState { RecipientEmail = "admin@example.com", LastDigestUtc = DateTime.UtcNow.AddHours(-1) }
        );
        var n = await AddNotificationAsync(
            h.Notifications,
            NotificationAudience.Administrators,
            DateTime.UtcNow.AddHours(-2)
        );

        await h.Build().RunOnceAsync(CancellationToken.None);

        Assert.Empty(h.CapturedJobs);
        // Left un-escalated so a later sweep (past the min interval) can still email it.
        var fresh = await h.Notifications.GetByIdAsync(n.Id);
        Assert.Null(fresh!.EscalatedUtc);
    }

    [Fact]
    public async Task Due_recipient_is_digested_while_throttled_recipient_is_skipped()
    {
        var h = new Harness();
        h.RecipientsFor(NotificationAudience.Administrators, "due@example.com", "throttled@example.com");
        // throttled@ got a digest 1h ago (< 6h interval); due@ has never been digested.
        await h.DigestState.UpsertAsync(
            new NotificationDigestState { RecipientEmail = "throttled@example.com", LastDigestUtc = DateTime.UtcNow.AddHours(-1) }
        );
        var n = await AddNotificationAsync(h.Notifications, NotificationAudience.Administrators, DateTime.UtcNow.AddHours(-2));

        await h.Build().RunOnceAsync(CancellationToken.None);

        // Each recipient is throttled independently: the due one gets a one-recipient digest, the
        // throttled one is not emailed this sweep (it still sees the item in-app).
        var job = Assert.Single(h.CapturedJobs);
        Assert.Equal("due@example.com", Assert.Single(job.Recipients).Email);

        // The item is escalated once (the throttled recipient won't get it by email, by design).
        var stamped = await h.Notifications.GetByIdAsync(n.Id);
        Assert.NotNull(stamped!.EscalatedUtc);

        Assert.NotNull(await h.DigestState.GetAsync("due@example.com"));
    }

    [Fact]
    public async Task Throttled_recipient_owed_an_escalated_item_logs_a_warning()
    {
        // A throttled recipient sharing an item with a due recipient never gets it by email (escalation is
        // stamped once globally). Benign, but it must be observable so a "missed email" report is
        // diagnosable - assert the warning names the recipient and the dropped item.
        var h = new Harness();
        h.RecipientsFor(NotificationAudience.Administrators, "due@example.com", "throttled@example.com");
        await h.DigestState.UpsertAsync(
            new NotificationDigestState { RecipientEmail = "throttled@example.com", LastDigestUtc = DateTime.UtcNow.AddHours(-1) }
        );
        var n = await AddNotificationAsync(h.Notifications, NotificationAudience.Administrators, DateTime.UtcNow.AddHours(-2));

        await h.Build().RunOnceAsync(CancellationToken.None);

        // The item was emailed to the due recipient (so it is not an orphan), but dropped from the
        // throttled recipient's email.
        var warning = Assert.Single(
            h.Logger.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("throttled@example.com")
        );
        Assert.Contains(n.Id.ToString(), warning.Message);
        Assert.Contains("in-app only", warning.Message);
        // It is not misreported as an orphan (it was emailed to the due recipient).
        Assert.DoesNotContain(h.Logger.Entries, e => e.Message.Contains("not queued to any recipient"));
    }

    [Fact]
    public async Task Item_stamped_but_never_emailed_after_a_send_failure_logs_a_warning()
    {
        // The at-most-once stamp happens before the digest is persisted; if the send fails transiently the
        // item stays escalated but reaches no one by email. That drop must be observable.
        var h = new Harness();
        h.RecipientsFor(NotificationAudience.Administrators, "admin@example.com");
        // Fail the digest upload so PersistJobAsync throws after the item is already stamped.
        h.FileStore
            .Setup(f => f.UploadAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>()))
            .ThrowsAsync(new IOException("blob store unavailable"));
        var n = await AddNotificationAsync(h.Notifications, NotificationAudience.Administrators, DateTime.UtcNow.AddHours(-2));

        await h.Build().RunOnceAsync(CancellationToken.None);

        // No job was queued, but the item was stamped escalated (so it won't resurface) - the drop is real.
        Assert.Empty(h.CapturedJobs);
        Assert.NotNull((await h.Notifications.GetByIdAsync(n.Id))!.EscalatedUtc);

        var warning = Assert.Single(
            h.Logger.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("not queued to any recipient")
        );
        Assert.Contains(n.Id.ToString(), warning.Message);
    }

    [Fact]
    public async Task Item_persisted_but_enqueue_fails_is_not_reported_as_orphan()
    {
        // A persisted Queued job is recoverable via EmailJobProcessor.ResumeIncompleteJobsAsync, so an
        // enqueue failure *after* the job is persisted (e.g. the queue's channel is completed during
        // shutdown) does not orphan the item - it must not be warned about. This locks the durability
        // boundary: emailedIds is recorded when the job persists, before the enqueue that fails here.
        var h = new Harness();
        h.RecipientsFor(NotificationAudience.Administrators, "admin@example.com");
        // Complete the queue so EnqueueAsync throws ChannelClosedException after PersistJobAsync persists it.
        h.Queue.CompleteWriter();
        var n = await AddNotificationAsync(h.Notifications, NotificationAudience.Administrators, DateTime.UtcNow.AddHours(-2));

        await h.Build().RunOnceAsync(CancellationToken.None);

        // The job was persisted (captured) and the item stamped, but the enqueue threw.
        Assert.Single(h.CapturedJobs);
        Assert.NotNull((await h.Notifications.GetByIdAsync(n.Id))!.EscalatedUtc);
        // Confirm the enqueue actually failed (otherwise this test would not be exercising the path).
        Assert.Contains(
            h.Logger.Entries,
            e => e.Level == LogLevel.Error && e.Message.Contains("Failed to send escalation digest")
        );
        // Crucially: no orphan warning, because the persisted job is recoverable.
        Assert.DoesNotContain(h.Logger.Entries, e => e.Message.Contains("not queued to any recipient"));
    }

    [Fact]
    public async Task Skips_when_all_recipients_recently_digested()
    {
        var h = new Harness();
        h.RecipientsFor(NotificationAudience.Administrators, "a@example.com", "b@example.com");
        await h.DigestState.UpsertAsync(new NotificationDigestState { RecipientEmail = "a@example.com", LastDigestUtc = DateTime.UtcNow.AddHours(-1) });
        await h.DigestState.UpsertAsync(new NotificationDigestState { RecipientEmail = "b@example.com", LastDigestUtc = DateTime.UtcNow.AddHours(-2) });
        var n = await AddNotificationAsync(h.Notifications, NotificationAudience.Administrators, DateTime.UtcNow.AddHours(-3));

        await h.Build().RunOnceAsync(CancellationToken.None);

        Assert.Empty(h.CapturedJobs);
        var fresh = await h.Notifications.GetByIdAsync(n.Id);
        Assert.Null(fresh!.EscalatedUtc);
    }

    [Fact]
    public async Task Sends_again_once_min_interval_elapsed()
    {
        var h = new Harness();
        h.RecipientsFor(NotificationAudience.Administrators, "admin@example.com");
        await h.DigestState.UpsertAsync(
            new NotificationDigestState { RecipientEmail = "admin@example.com", LastDigestUtc = DateTime.UtcNow.AddHours(-7) }
        );
        await AddNotificationAsync(h.Notifications, NotificationAudience.Administrators, DateTime.UtcNow.AddHours(-2));

        await h.Build().RunOnceAsync(CancellationToken.None);

        Assert.Single(h.CapturedJobs);
    }

    [Fact]
    public async Task No_recipients_leaves_notification_unescalated()
    {
        var h = new Harness();
        // Resolver returns empty for Administrators (the default).
        var n = await AddNotificationAsync(
            h.Notifications,
            NotificationAudience.Administrators,
            DateTime.UtcNow.AddHours(-2)
        );

        await h.Build().RunOnceAsync(CancellationToken.None);

        Assert.Empty(h.CapturedJobs);
        var fresh = await h.Notifications.GetByIdAsync(n.Id);
        Assert.Null(fresh!.EscalatedUtc);
    }

    [Fact]
    public async Task Batches_multiple_aged_items_into_one_digest()
    {
        var h = new Harness();
        h.RecipientsFor(NotificationAudience.Administrators, "admin@example.com");
        var a = await AddNotificationAsync(h.Notifications, NotificationAudience.Administrators, DateTime.UtcNow.AddHours(-3));
        var b = await AddNotificationAsync(h.Notifications, NotificationAudience.Administrators, DateTime.UtcNow.AddHours(-2));

        await h.Build().RunOnceAsync(CancellationToken.None);

        var job = Assert.Single(h.CapturedJobs);
        var freshA = await h.Notifications.GetByIdAsync(a.Id);
        var freshB = await h.Notifications.GetByIdAsync(b.Id);
        Assert.Equal(job.Id, freshA!.EscalationJobId);
        Assert.Equal(job.Id, freshB!.EscalationJobId);
    }

    [Fact]
    public async Task Digest_caps_items_stamps_only_shown_and_defers_overflow()
    {
        var h = new Harness();
        h.Options.MaxItemsPerDigest = 2;
        h.RecipientsFor(NotificationAudience.Administrators, "admin@example.com");
        // created[0] is newest (-2h), created[2] is oldest (-4h). Oldest-first, the cap shows the two
        // oldest and defers the newest.
        var created = new List<Notification>();
        for (var i = 0; i < 3; i++)
            created.Add(await AddNotificationAsync(h.Notifications, NotificationAudience.Administrators, DateTime.UtcNow.AddHours(-2 - i)));

        await h.Build().RunOnceAsync(CancellationToken.None);

        Assert.Single(h.CapturedJobs);
        Assert.NotNull(h.UploadedHtml);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(h.UploadedHtml!, "<li>").Count);
        Assert.Contains("and 1 more", h.UploadedHtml!);

        // Only the two shown (oldest) items are stamped escalated; the overflow item is left
        // un-escalated so a later sweep emails it rather than dropping it from the email channel.
        var states = new List<Notification>();
        foreach (var c in created)
            states.Add((await h.Notifications.GetByIdAsync(c.Id))!);
        Assert.Equal(2, states.Count(n => n.EscalatedUtc != null));
        var deferred = Assert.Single(states, n => n.EscalatedUtc == null);
        Assert.Equal(created[0].Id, deferred.Id); // the newest item is the one beyond the cap
    }

    [Fact]
    public async Task Overflow_item_is_emailed_on_a_later_sweep()
    {
        var h = new Harness();
        h.Options.MaxItemsPerDigest = 2;
        h.RecipientsFor(NotificationAudience.Administrators, "admin@example.com");
        var created = new List<Notification>();
        for (var i = 0; i < 3; i++)
            created.Add(await AddNotificationAsync(h.Notifications, NotificationAudience.Administrators, DateTime.UtcNow.AddHours(-2 - i)));

        await h.Build().RunOnceAsync(CancellationToken.None);
        Assert.Single(h.CapturedJobs);

        // Simulate the throttle interval elapsing so the recipient is due again.
        await h.DigestState.UpsertAsync(
            new NotificationDigestState { RecipientEmail = "admin@example.com", LastDigestUtc = DateTime.UtcNow.AddHours(-7) }
        );

        await h.Build().RunOnceAsync(CancellationToken.None);

        // A second digest carries the previously-deferred overflow item; all three end up escalated.
        Assert.Equal(2, h.CapturedJobs.Count);
        Assert.Equal("COHAD: 1 item(s) need attention", h.CapturedJobs[1].Subject);
        foreach (var c in created)
            Assert.NotNull((await h.Notifications.GetByIdAsync(c.Id))!.EscalatedUtc);
    }

    [Fact]
    public async Task Overflow_item_stamped_for_another_recipient_is_not_promised_as_a_follow_up()
    {
        // An admin's overflow item that a committee moderator emails (and thus stamps) this same sweep is
        // now escalated and will never reach the admin again — so the admin's digest must NOT count it as
        // a "more waiting; you'll receive them in a follow-up" item.
        var h = new Harness();
        h.Options.MaxItemsPerDigest = 2;
        h.Committees
            .Setup(r => r.GetAllAsync())
            .ReturnsAsync(new List<Committee> { new Committee { Id = "welcome", DisplayName = "Welcome" } });
        var committeeAudience = NotificationAudience.Committee("welcome");
        // The admin belongs to both audiences; the moderator only to the committee.
        h.RecipientsFor(NotificationAudience.Administrators, "admin@example.com");
        h.RecipientsFor(committeeAudience, "admin@example.com", "mod@example.com");

        // Two older admin items fill the admin's cap; the committee item is the admin's overflow but is
        // within the moderator's cap.
        await AddNotificationAsync(h.Notifications, NotificationAudience.Administrators, DateTime.UtcNow.AddHours(-4));
        await AddNotificationAsync(h.Notifications, NotificationAudience.Administrators, DateTime.UtcNow.AddHours(-3));
        var committeeItem = await AddNotificationAsync(
            h.Notifications,
            committeeAudience,
            DateTime.UtcNow.AddHours(-2),
            type: NotificationType.HeldMessage
        );

        await h.Build().RunOnceAsync(CancellationToken.None);

        // The committee item was emailed (and stamped) via the moderator.
        Assert.NotNull((await h.Notifications.GetByIdAsync(committeeItem.Id))!.EscalatedUtc);

        // The admin's digest shows its two capped items and does NOT falsely promise a follow-up for the
        // committee item that went to the moderator.
        var adminJob = h.CapturedJobs.Single(j => j.Recipients.Any(r => r.Email == "admin@example.com"));
        var adminHtml = h.UploadedByPath[adminJob.ContentBlobPath];
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(adminHtml, "<li>").Count);
        Assert.DoesNotContain("more waiting", adminHtml);
    }

    [Fact]
    public async Task Digest_deep_links_items_when_base_url_configured()
    {
        var h = new Harness { AppBaseUrl = "https://www.cohad.org/" };
        var committeeAudience = NotificationAudience.Committee("welcome");
        h.Committees
            .Setup(r => r.GetAllAsync())
            .ReturnsAsync(new List<Committee> { new Committee { Id = "welcome", DisplayName = "Welcome" } });
        h.RecipientsFor(committeeAudience, "mod@example.com");
        await h.Notifications.AddAsync(new Notification
        {
            Id = Notification.DeterministicId(NotificationTargetType.HeldMessage, "abc"),
            Type = NotificationType.HeldMessage,
            AudienceKey = committeeAudience,
            TargetType = NotificationTargetType.HeldMessage,
            TargetId = "abc",
            Title = "Held committee email",
            Summary = "Welcome: from Stranger — Hello",
            DeepLink = "/manage/approvals?message=abc",
            CreatedUtc = DateTime.UtcNow.AddHours(-2),
        });

        await h.Build().RunOnceAsync(CancellationToken.None);

        Assert.Single(h.CapturedJobs);
        Assert.NotNull(h.UploadedHtml);
        // The base URL's trailing slash is normalized away, so the item links to an absolute URL.
        Assert.Contains(
            "<a href=\"https://www.cohad.org/manage/approvals?message=abc\"><strong>Held committee email</strong></a>",
            h.UploadedHtml!
        );
    }

    [Fact]
    public async Task Digest_omits_links_when_no_base_url_configured()
    {
        var h = new Harness(); // AppBaseUrl null
        h.RecipientsFor(NotificationAudience.Administrators, "admin@example.com");
        await h.Notifications.AddAsync(new Notification
        {
            Id = Notification.DeterministicId(NotificationTargetType.User, "u1"),
            Type = NotificationType.Registration,
            AudienceKey = NotificationAudience.Administrators,
            TargetType = NotificationTargetType.User,
            TargetId = "u1",
            Title = "New user registered",
            Summary = "Jane Doe",
            DeepLink = "/manage/users",
            CreatedUtc = DateTime.UtcNow.AddHours(-2),
        });

        await h.Build().RunOnceAsync(CancellationToken.None);

        Assert.NotNull(h.UploadedHtml);
        // Without a base URL the digest degrades to a plain bold title (no anchor tags at all).
        Assert.DoesNotContain("<a href", h.UploadedHtml!);
        Assert.Contains("<strong>New user registered</strong>", h.UploadedHtml!);
    }

    [Fact]
    public async Task Escalates_committee_audience_to_its_moderators()
    {
        var h = new Harness();
        h.Committees
            .Setup(r => r.GetAllAsync())
            .ReturnsAsync(new List<Committee> { new Committee { Id = "welcome", DisplayName = "Welcome" } });
        var committeeAudience = NotificationAudience.Committee("welcome");
        h.RecipientsFor(committeeAudience, "member@example.com");
        var n = await AddNotificationAsync(
            h.Notifications,
            committeeAudience,
            DateTime.UtcNow.AddHours(-2),
            type: NotificationType.HeldMessage
        );

        await h.Build().RunOnceAsync(CancellationToken.None);

        var job = Assert.Single(h.CapturedJobs);
        Assert.Equal("member@example.com", Assert.Single(job.Recipients).Email);
        var stamped = await h.Notifications.GetByIdAsync(n.Id);
        Assert.NotNull(stamped!.EscalatedUtc);
    }

    [Fact]
    public async Task Recipient_in_two_audiences_gets_one_combined_digest()
    {
        var h = new Harness();
        h.Committees
            .Setup(r => r.GetAllAsync())
            .ReturnsAsync(new List<Committee> { new Committee { Id = "welcome", DisplayName = "Welcome" } });
        var committeeAudience = NotificationAudience.Committee("welcome");
        // The same person is a recipient for both audiences (e.g. an admin who also moderates a committee).
        h.RecipientsFor(NotificationAudience.Administrators, "shared@example.com");
        h.RecipientsFor(committeeAudience, "shared@example.com");

        var adminItem = await AddNotificationAsync(h.Notifications, NotificationAudience.Administrators, DateTime.UtcNow.AddHours(-2));
        var committeeItem = await AddNotificationAsync(h.Notifications, committeeAudience, DateTime.UtcNow.AddHours(-2), type: NotificationType.HeldMessage);

        await h.Build().RunOnceAsync(CancellationToken.None);

        // One combined digest to the shared recipient covering BOTH audiences' items; both escalated.
        var job = Assert.Single(h.CapturedJobs);
        Assert.Equal("shared@example.com", Assert.Single(job.Recipients).Email);
        Assert.Equal("COHAD: 2 item(s) need attention", job.Subject);
        Assert.NotNull((await h.Notifications.GetByIdAsync(adminItem.Id))!.EscalatedUtc);
        Assert.NotNull((await h.Notifications.GetByIdAsync(committeeItem.Id))!.EscalatedUtc);
    }

    [Fact]
    public async Task Resolved_notification_is_not_escalated()
    {
        var h = new Harness();
        h.RecipientsFor(NotificationAudience.Administrators, "admin@example.com");
        var n = await AddNotificationAsync(h.Notifications, NotificationAudience.Administrators, DateTime.UtcNow.AddHours(-2));
        // Resolve it before the sweep — it should be excluded from the unresolved query.
        n.ResolvedUtc = DateTime.UtcNow;
        await h.Notifications.UpsertAsync(n);

        await h.Build().RunOnceAsync(CancellationToken.None);

        Assert.Empty(h.CapturedJobs);
    }

    // The next two tests simulate a concurrent resolve landing AFTER the sweep's query but before the
    // stamp, which the in-memory MockNotificationRepository can't express (its query and point read are
    // always consistent), so they use a Moq repository whose GetByIdAsync diverges from the query.

    private static Notification AgedNotification(DateTime createdUtc) => new()
    {
        Id = Guid.NewGuid(),
        Type = NotificationType.Registration,
        AudienceKey = NotificationAudience.Administrators,
        TargetType = NotificationTargetType.User,
        TargetId = Guid.NewGuid().ToString(),
        Title = "New user registered",
        Summary = "Jane Doe",
        CreatedUtc = createdUtc,
        ETag = "1",
    };

    private static NotificationEscalationRunner BuildRunnerWithRepo(
        INotificationRepository repo,
        out List<EmailJob> capturedJobs,
        out MockNotificationDigestStateRepository digestState
    )
    {
        capturedJobs = new List<EmailJob>();
        var captured = capturedJobs;
        digestState = new MockNotificationDigestStateRepository();

        var resolver = new Mock<INotificationRecipientResolver>();
        resolver
            .Setup(r => r.ResolveAudienceEmailsAsync(NotificationAudience.Administrators, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "admin@example.com" });
        resolver
            .Setup(r => r.ResolveAudienceEmailsAsync(It.Is<string>(a => a != NotificationAudience.Administrators), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        var committees = new Mock<ICommitteeRepository>();
        committees.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Committee>());

        var emailJobs = new Mock<IEmailJobRepository>();
        emailJobs.Setup(r => r.AddAsync(It.IsAny<EmailJob>())).Callback<EmailJob>(j => captured.Add(j)).Returns(Task.CompletedTask);

        var fileStore = new Mock<IDocumentFileStore>();
        fileStore.Setup(f => f.UploadAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>())).Returns(Task.CompletedTask);

        var options = new NotificationEscalationOptions { Enabled = true, GracePeriodMinutes = 30, MinDigestIntervalHours = 6, MaxItemsPerDigest = 10 };

        return new NotificationEscalationRunner(
            repo,
            resolver.Object,
            committees.Object,
            digestState,
            emailJobs.Object,
            fileStore.Object,
            new EmailJobQueue(),
            Microsoft.Extensions.Options.Options.Create(options),
            BuildConfig(null),
            NullLogger<NotificationEscalationRunner>.Instance
        );
    }

    [Fact]
    public async Task Digest_excludes_item_resolved_after_the_query()
    {
        var keep = AgedNotification(DateTime.UtcNow.AddHours(-3));
        var raced = AgedNotification(DateTime.UtcNow.AddHours(-2));

        var repo = new Mock<INotificationRepository>();
        repo.Setup(r => r.GetUnescalatedByAudienceOldestFirstAsync(NotificationAudience.Administrators, It.IsAny<int>()))
            .ReturnsAsync(new List<Notification> { keep, raced });
        repo.Setup(r => r.GetUnescalatedByAudienceOldestFirstAsync(It.Is<string>(a => a != NotificationAudience.Administrators), It.IsAny<int>()))
            .ReturnsAsync(new List<Notification>());
        repo.Setup(r => r.GetByIdAsync(keep.Id)).ReturnsAsync(keep);
        // 'raced' was resolved by a human between the query and the stamp.
        repo.Setup(r => r.GetByIdAsync(raced.Id))
            .ReturnsAsync(new Notification { Id = raced.Id, AudienceKey = raced.AudienceKey, ResolvedUtc = DateTime.UtcNow, ETag = "2" });
        repo.Setup(r => r.UpsertWithEtagAsync(It.IsAny<Notification>())).Returns(Task.CompletedTask);

        var capturedHtml = (string?)null;
        var runner = BuildRunnerWithRepoCapturingHtml(repo.Object, out var capturedJobs, out _, html => capturedHtml = html);

        await runner.RunOnceAsync(CancellationToken.None);

        var job = Assert.Single(capturedJobs);
        Assert.Equal("COHAD: 1 item(s) need attention", job.Subject);
        Assert.NotNull(capturedHtml);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(capturedHtml!, "<li>"));
        // Only the surviving item was stamped (UpsertWithEtag called exactly once).
        repo.Verify(r => r.UpsertWithEtagAsync(It.Is<Notification>(n => n.Id == keep.Id)), Times.Once);
        repo.Verify(r => r.UpsertWithEtagAsync(It.Is<Notification>(n => n.Id == raced.Id)), Times.Never);
    }

    [Fact]
    public async Task No_job_or_throttle_when_all_items_resolved_after_the_query()
    {
        var raced = AgedNotification(DateTime.UtcNow.AddHours(-2));

        var repo = new Mock<INotificationRepository>();
        repo.Setup(r => r.GetUnescalatedByAudienceOldestFirstAsync(NotificationAudience.Administrators, It.IsAny<int>()))
            .ReturnsAsync(new List<Notification> { raced });
        repo.Setup(r => r.GetUnescalatedByAudienceOldestFirstAsync(It.Is<string>(a => a != NotificationAudience.Administrators), It.IsAny<int>()))
            .ReturnsAsync(new List<Notification>());
        repo.Setup(r => r.GetByIdAsync(raced.Id))
            .ReturnsAsync(new Notification { Id = raced.Id, AudienceKey = raced.AudienceKey, ResolvedUtc = DateTime.UtcNow, ETag = "2" });

        var runner = BuildRunnerWithRepo(repo.Object, out var capturedJobs, out var digestState);

        await runner.RunOnceAsync(CancellationToken.None);

        // Nothing stamped → no job and no throttle write.
        Assert.Empty(capturedJobs);
        Assert.Null(await digestState.GetAsync("admin@example.com"));
        repo.Verify(r => r.UpsertWithEtagAsync(It.IsAny<Notification>()), Times.Never);
    }

    [Fact]
    public async Task Transient_stamp_failure_on_one_item_does_not_abort_the_others()
    {
        var keep = AgedNotification(DateTime.UtcNow.AddHours(-3));
        var failing = AgedNotification(DateTime.UtcNow.AddHours(-2));

        var repo = new Mock<INotificationRepository>();
        repo.Setup(r => r.GetUnescalatedByAudienceOldestFirstAsync(NotificationAudience.Administrators, It.IsAny<int>()))
            .ReturnsAsync(new List<Notification> { keep, failing });
        repo.Setup(r => r.GetUnescalatedByAudienceOldestFirstAsync(It.Is<string>(a => a != NotificationAudience.Administrators), It.IsAny<int>()))
            .ReturnsAsync(new List<Notification>());
        repo.Setup(r => r.GetByIdAsync(keep.Id)).ReturnsAsync(keep);
        repo.Setup(r => r.GetByIdAsync(failing.Id)).ReturnsAsync(failing);
        repo.Setup(r => r.UpsertWithEtagAsync(It.Is<Notification>(n => n.Id == keep.Id))).Returns(Task.CompletedTask);
        // A transient (non-412) Cosmos error stamping the second item.
        repo.Setup(r => r.UpsertWithEtagAsync(It.Is<Notification>(n => n.Id == failing.Id)))
            .ThrowsAsync(new Microsoft.Azure.Cosmos.CosmosException("transient", System.Net.HttpStatusCode.ServiceUnavailable, 0, string.Empty, 0));

        var runner = BuildRunnerWithRepo(repo.Object, out var capturedJobs, out _);

        // Does not throw despite the per-item failure.
        await runner.RunOnceAsync(CancellationToken.None);

        // The surviving item is still digested; the failing one is simply left for a later sweep.
        var job = Assert.Single(capturedJobs);
        Assert.Equal("COHAD: 1 item(s) need attention", job.Subject);
        repo.Verify(r => r.UpsertWithEtagAsync(It.Is<Notification>(n => n.Id == keep.Id)), Times.Once);
    }

    private static NotificationEscalationRunner BuildRunnerWithRepoCapturingHtml(
        INotificationRepository repo,
        out List<EmailJob> capturedJobs,
        out MockNotificationDigestStateRepository digestState,
        Action<string> onHtml
    )
    {
        capturedJobs = new List<EmailJob>();
        var captured = capturedJobs;
        digestState = new MockNotificationDigestStateRepository();

        var resolver = new Mock<INotificationRecipientResolver>();
        resolver
            .Setup(r => r.ResolveAudienceEmailsAsync(NotificationAudience.Administrators, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "admin@example.com" });
        resolver
            .Setup(r => r.ResolveAudienceEmailsAsync(It.Is<string>(a => a != NotificationAudience.Administrators), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        var committees = new Mock<ICommitteeRepository>();
        committees.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Committee>());

        var emailJobs = new Mock<IEmailJobRepository>();
        emailJobs.Setup(r => r.AddAsync(It.IsAny<EmailJob>())).Callback<EmailJob>(j => captured.Add(j)).Returns(Task.CompletedTask);

        var fileStore = new Mock<IDocumentFileStore>();
        fileStore
            .Setup(f => f.UploadAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>()))
            .Callback<string, Stream, string>((_, stream, _) =>
            {
                using var sr = new StreamReader(stream, leaveOpen: true);
                onHtml(sr.ReadToEnd());
            })
            .Returns(Task.CompletedTask);

        var options = new NotificationEscalationOptions { Enabled = true, GracePeriodMinutes = 30, MinDigestIntervalHours = 6, MaxItemsPerDigest = 10 };

        return new NotificationEscalationRunner(
            repo,
            resolver.Object,
            committees.Object,
            digestState,
            emailJobs.Object,
            fileStore.Object,
            new EmailJobQueue(),
            Microsoft.Extensions.Options.Options.Create(options),
            BuildConfig(null),
            NullLogger<NotificationEscalationRunner>.Instance
        );
    }
}
