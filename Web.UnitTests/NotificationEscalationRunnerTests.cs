using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
                    (_, stream, _) =>
                    {
                        using var sr = new StreamReader(stream, leaveOpen: true);
                        UploadedHtml = sr.ReadToEnd();
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
                NullLogger<NotificationEscalationRunner>.Instance
            );
    }

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
    public async Task Sends_to_all_recipients_when_any_is_due_even_if_some_throttled()
    {
        var h = new Harness();
        h.RecipientsFor(NotificationAudience.Administrators, "due@example.com", "throttled@example.com");
        // throttled@ got a digest 1h ago (< 6h interval); due@ has never been digested.
        await h.DigestState.UpsertAsync(
            new NotificationDigestState { RecipientEmail = "throttled@example.com", LastDigestUtc = DateTime.UtcNow.AddHours(-1) }
        );
        var n = await AddNotificationAsync(h.Notifications, NotificationAudience.Administrators, DateTime.UtcNow.AddHours(-2));

        await h.Build().RunOnceAsync(CancellationToken.None);

        // The digest goes to BOTH recipients (not just the due one), so the throttled recipient does
        // not permanently lose the item once it is stamped escalated.
        var job = Assert.Single(h.CapturedJobs);
        Assert.Equal(2, job.Recipients.Count);
        Assert.Contains(job.Recipients, r => r.Email == "due@example.com");
        Assert.Contains(job.Recipients, r => r.Email == "throttled@example.com");

        var stamped = await h.Notifications.GetByIdAsync(n.Id);
        Assert.NotNull(stamped!.EscalatedUtc);

        // Throttle is refreshed for everyone who was emailed.
        Assert.NotNull(await h.DigestState.GetAsync("due@example.com"));
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
    public async Task Digest_caps_items_and_notes_overflow()
    {
        var h = new Harness();
        h.Options.MaxItemsPerDigest = 2;
        h.RecipientsFor(NotificationAudience.Administrators, "admin@example.com");
        for (var i = 0; i < 3; i++)
            await AddNotificationAsync(h.Notifications, NotificationAudience.Administrators, DateTime.UtcNow.AddHours(-2 - i));

        await h.Build().RunOnceAsync(CancellationToken.None);

        Assert.Single(h.CapturedJobs);
        Assert.NotNull(h.UploadedHtml);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(h.UploadedHtml!, "<li>").Count);
        Assert.Contains("and 1 more", h.UploadedHtml!);
    }

    [Fact]
    public async Task Escalates_committee_audience_to_committee_members()
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
}
