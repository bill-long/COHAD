using Web.Models;
using Web.PresentationModels;
using Web.Services;
using Xunit;

namespace Web.UnitTests;

/// <summary>
/// Locks the rules that separate "who wrote this message" from "what address it was sent as".
/// A committee forward is sent as the committee mailbox, so <see cref="EmailJob.FromEmail"/> alone
/// misreports the author - these are the rules the job list and detail pages read.
/// </summary>
public class EmailJobPartiesTests
{
    // ── CommitteeForwardJob.ApplyOriginator ────────────────────────────────

    [Fact]
    public void ApplyOriginator_records_the_author_separately_from_the_sending_mailbox()
    {
        var job = new EmailJob
        {
            Category = EmailJob.CommitteeForwardCategory,
            FromEmail = "architectural@cohad.org",
            FromDisplay = "Architectural Committee",
        };

        CommitteeForwardJob.ApplyOriginator(job, "Architectural Committee", "jane@example.com", "Jane Doe");

        // The outgoing From is untouched - it is also part of the forwarding idempotency key.
        Assert.Equal("architectural@cohad.org", job.FromEmail);
        Assert.Equal("jane@example.com", job.OriginalSenderEmail);
        Assert.Equal("Jane Doe", job.OriginalSenderDisplay);
        Assert.Equal("jane@example.com", job.ReplyToEmail);
        Assert.Equal("Jane Doe", job.ReplyToDisplay);
        Assert.Equal("Architectural Committee forwarding members", job.ToDisplay);
    }

    [Fact]
    public void ApplyOriginator_leaves_sender_fields_null_when_the_message_has_no_sender_address()
    {
        var job = new EmailJob { Category = EmailJob.CommitteeForwardCategory };

        CommitteeForwardJob.ApplyOriginator(job, "Architectural Committee", "   ", "Mailer Daemon");

        Assert.Null(job.OriginalSenderEmail);
        Assert.Null(job.OriginalSenderDisplay);
        Assert.Null(job.ReplyToEmail);
        Assert.Null(job.ReplyToDisplay);
        // The audience is still known even when the author is not.
        Assert.Equal("Architectural Committee forwarding members", job.ToDisplay);
    }

    [Fact]
    public void ApplyOriginator_falls_back_to_a_generic_audience_when_the_committee_has_no_display_name()
    {
        var job = new EmailJob();

        CommitteeForwardJob.ApplyOriginator(job, "", "jane@example.com", "Jane Doe");

        Assert.Equal("Committee forwarding members", job.ToDisplay);
    }

    [Fact]
    public void ForCommitteeForward_says_forwarding_members_because_only_opted_in_members_receive()
    {
        // Both forwarding paths select recipients with Where(m => m.ReceivesForwardedEmail), so the
        // wider word "members" would describe people who were never addressed.
        Assert.Equal("Architectural Committee forwarding members", EmailAudience.ForCommitteeForward("Architectural Committee"));
    }

    [Fact]
    public void Every_audience_description_names_its_distinguishing_word_first()
    {
        // The wording exists to survive truncation in a narrow table cell, which only works while the
        // word that tells two audiences apart comes first.
        Assert.StartsWith("Board", EmailAudience.ForCommitteeSend("Board"));
        Assert.StartsWith("Garden Club", EmailAudience.ForCommitteeSend("Garden Club"));
        Assert.StartsWith("Architectural Committee", EmailAudience.ForCommitteeForward("Architectural Committee"));
        Assert.StartsWith("Test", EmailAudience.TestRecipients);
        Assert.StartsWith("Notification", EmailAudience.NotificationModerator);
    }

    [Fact]
    public void Audience_descriptions_fall_back_when_the_committee_has_no_name()
    {
        Assert.Equal("Opt-in residents", EmailAudience.ForCommitteeSend(" "));
        Assert.Equal("Committee forwarding members", EmailAudience.ForCommitteeForward(" "));
    }

    // ── EmailJob.ResolveOriginalSender ─────────────────────────────────────

    [Fact]
    public void ResolveOriginalSender_returns_null_for_a_message_composed_in_the_app()
    {
        var job = new EmailJob
        {
            Category = "board",
            FromEmail = "board@cohad.org",
            FromDisplay = "COHAD Board",
        };

        Assert.Null(job.ResolveOriginalSender());
    }

    [Fact]
    public void ResolveOriginalSender_prefers_the_stored_original_sender()
    {
        var job = new EmailJob
        {
            Category = EmailJob.CommitteeForwardCategory,
            OriginalSenderEmail = "jane@example.com",
            OriginalSenderDisplay = "Jane Doe",
            ReplyToEmail = "stale@example.com",
            ReplyToDisplay = "Stale",
        };

        var sender = job.ResolveOriginalSender();

        Assert.NotNull(sender);
        Assert.Equal("jane@example.com", sender!.Value.Email);
        Assert.Equal("Jane Doe", sender.Value.Display);
    }

    [Fact]
    public void ResolveOriginalSender_falls_back_to_reply_to_for_forwards_predating_the_field()
    {
        // Jobs created before OriginalSender* existed recorded the author only as the Reply-To.
        var job = new EmailJob
        {
            Category = EmailJob.CommitteeForwardCategory,
            FromEmail = "architectural@cohad.org",
            ReplyToEmail = "jane@example.com",
            ReplyToDisplay = "Jane Doe",
        };

        var sender = job.ResolveOriginalSender();

        Assert.NotNull(sender);
        Assert.Equal("jane@example.com", sender!.Value.Email);
        Assert.Equal("Jane Doe", sender.Value.Display);
    }

    [Fact]
    public void ResolveOriginalSender_ignores_reply_to_on_a_job_that_is_not_a_forward()
    {
        // On an ordinary send a Reply-To is a routing preference, not an author.
        var job = new EmailJob
        {
            Category = "board",
            FromEmail = "board@cohad.org",
            ReplyToEmail = "president@example.com",
            ReplyToDisplay = "Board President",
        };

        Assert.Null(job.ResolveOriginalSender());
    }

    // ── DTO mapping ────────────────────────────────────────────────────────

    private static EmailJob ForwardedJob() =>
        new EmailJob
        {
            Category = EmailJob.CommitteeForwardCategory,
            FromEmail = "architectural@cohad.org",
            FromDisplay = "Architectural Committee",
            ToDisplay = "Architectural Committee forwarding members",
            OriginalSenderEmail = "jane@example.com",
            OriginalSenderDisplay = "Jane Doe",
        };

    [Fact]
    public void Summary_and_detail_report_the_same_parties_for_a_forwarded_job()
    {
        var job = ForwardedJob();

        var summary = EmailJobSummary.FromJob(job, includeOriginalSender: true);
        var detail = EmailJobDetail.FromJob(job, includeOriginalSender: true);

        Assert.Equal("architectural@cohad.org", summary.FromEmail);
        Assert.Equal("Architectural Committee forwarding members", summary.ToDisplay);
        Assert.Equal("jane@example.com", summary.OriginalSenderEmail);
        Assert.Equal("Jane Doe", summary.OriginalSenderDisplay);

        Assert.Equal(summary.FromEmail, detail.FromEmail);
        Assert.Equal(summary.ToDisplay, detail.ToDisplay);
        Assert.Equal(summary.OriginalSenderEmail, detail.OriginalSenderEmail);
        Assert.Equal(summary.OriginalSenderDisplay, detail.OriginalSenderDisplay);
    }

    [Fact]
    public void Dtos_withhold_the_original_sender_by_default()
    {
        // The job endpoints are open to every "EmailSender" role, so a caller who is not an
        // Administrator must not learn who wrote to some other committee. Defaulting to withheld
        // means a new call site cannot leak it by omission.
        var job = ForwardedJob();

        var summary = EmailJobSummary.FromJob(job);
        var detail = EmailJobDetail.FromJob(job);

        Assert.Null(summary.OriginalSenderEmail);
        Assert.Null(summary.OriginalSenderDisplay);
        Assert.Null(detail.OriginalSenderEmail);
        Assert.Null(detail.OriginalSenderDisplay);
        // Everything that is not the third party's identity still comes through.
        Assert.Equal("architectural@cohad.org", summary.FromEmail);
        Assert.Equal("Architectural Committee forwarding members", summary.ToDisplay);
    }

    [Fact]
    public void Detail_carries_every_summary_field_because_it_extends_the_summary()
    {
        var job = ForwardedJob();
        job.Subject = "Fwd: Repaint request";
        job.CreatedByDisplayName = "Committee Mail Poller";
        job.TotalRecipients = 1;
        job.SentCount = 1;
        job.Recipients.Add(new EmailJobRecipient { Email = "member@example.com" });

        var detail = EmailJobDetail.FromJob(job, includeOriginalSender: true);

        Assert.IsAssignableFrom<EmailJobSummary>(detail);
        // Every field the list shows, so a Populate that stopped copying one would fail here rather
        // than silently letting the two views disagree.
        Assert.Equal(job.Id, detail.Id);
        Assert.Equal(job.Status, detail.Status);
        Assert.Equal(job.Category, detail.Category);
        Assert.Equal(job.FromEmail, detail.FromEmail);
        Assert.Equal(job.FromDisplay, detail.FromDisplay);
        Assert.Equal(job.ToDisplay, detail.ToDisplay);
        Assert.Equal(job.OriginalSenderEmail, detail.OriginalSenderEmail);
        Assert.Equal(job.OriginalSenderDisplay, detail.OriginalSenderDisplay);
        Assert.Equal(job.Subject, detail.Subject);
        Assert.Equal(job.CreatedUtc, detail.CreatedUtc);
        Assert.Equal(job.CreatedByDisplayName, detail.CreatedByDisplayName);
        Assert.Equal(job.TotalRecipients, detail.TotalRecipients);
        Assert.Equal(job.SentCount, detail.SentCount);
        Assert.Equal(job.FailedCount, detail.FailedCount);
        Assert.Equal(job.LastError, detail.LastError);
        // Plus the field that is the reason a detail DTO exists at all.
        Assert.Single(detail.Recipients);
        Assert.Equal("member@example.com", detail.Recipients[0].Email);
    }

    [Fact]
    public void Dtos_surface_the_reply_to_fallback_for_legacy_forwarded_jobs()
    {
        var job = new EmailJob
        {
            Category = EmailJob.CommitteeForwardCategory,
            FromEmail = "architectural@cohad.org",
            FromDisplay = "Architectural Committee",
            ReplyToEmail = "jane@example.com",
            ReplyToDisplay = "Jane Doe",
        };

        Assert.Equal("jane@example.com", EmailJobSummary.FromJob(job, includeOriginalSender: true).OriginalSenderEmail);
        Assert.Equal("jane@example.com", EmailJobDetail.FromJob(job, includeOriginalSender: true).OriginalSenderEmail);
        // Nothing was stored for the audience on these jobs; the client fills in a recipient count.
        Assert.Null(EmailJobDetail.FromJob(job, includeOriginalSender: true).ToDisplay);
    }

    [Fact]
    public void Dtos_leave_the_original_sender_empty_for_an_ordinary_send()
    {
        var job = new EmailJob
        {
            Category = "board",
            FromEmail = "board@cohad.org",
            FromDisplay = "COHAD Board",
            ToDisplay = "Board opt-in residents",
        };

        var summary = EmailJobSummary.FromJob(job, includeOriginalSender: true);

        Assert.Null(summary.OriginalSenderEmail);
        Assert.Null(summary.OriginalSenderDisplay);
        Assert.Equal("Board opt-in residents", summary.ToDisplay);
    }
}
