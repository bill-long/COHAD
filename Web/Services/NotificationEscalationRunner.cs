#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Web.Models;
using Web.Services.Repositories;

namespace Web.Services
{
    /// <summary>
    /// Performs a single escalation sweep: for each audience, finds unresolved notifications that have
    /// aged past the grace period without being escalated, batches them into one digest email per due
    /// recipient (throttled by <see cref="NotificationEscalationOptions.MinDigestIntervalHours"/>), and
    /// stamps the notifications as escalated so they are emailed at most once.
    /// </summary>
    /// <remarks>
    /// Split out of <see cref="NotificationEscalationService"/> as a scoped service so the sweep logic
    /// can be unit-tested directly with mocked repositories. Assumes a single sweeping instance: the
    /// escalated stamp guards against re-sending across sweeps, but two concurrent sweepers could both
    /// emit a digest before either stamps. Duplicate admin digests are low-impact and acceptable here.
    /// </remarks>
    public sealed class NotificationEscalationRunner
    {
        private readonly INotificationRepository _notificationRepository;
        private readonly INotificationRecipientResolver _recipientResolver;
        private readonly ICommitteeRepository _committeeRepository;
        private readonly INotificationDigestStateRepository _digestStateRepository;
        private readonly IEmailJobRepository _emailJobRepository;
        private readonly IDocumentFileStore _fileStore;
        private readonly EmailJobQueue _emailJobQueue;
        private readonly NotificationEscalationOptions _options;
        private readonly ILogger<NotificationEscalationRunner> _logger;

        private const string EscalationCategory = "notification-escalation";

        public NotificationEscalationRunner(
            INotificationRepository notificationRepository,
            INotificationRecipientResolver recipientResolver,
            ICommitteeRepository committeeRepository,
            INotificationDigestStateRepository digestStateRepository,
            IEmailJobRepository emailJobRepository,
            IDocumentFileStore fileStore,
            EmailJobQueue emailJobQueue,
            IOptions<NotificationEscalationOptions> options,
            ILogger<NotificationEscalationRunner> logger
        )
        {
            _notificationRepository = notificationRepository;
            _recipientResolver = recipientResolver;
            _committeeRepository = committeeRepository;
            _digestStateRepository = digestStateRepository;
            _emailJobRepository = emailJobRepository;
            _fileStore = fileStore;
            _emailJobQueue = emailJobQueue;
            _options = options.Value;
            _logger = logger;
        }

        public async Task RunOnceAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var now = DateTime.UtcNow;
            var graceCutoff = now - TimeSpan.FromMinutes(Math.Max(0, _options.GracePeriodMinutes));
            var minDigestInterval = TimeSpan.FromHours(Math.Max(0, _options.MinDigestIntervalHours));

            // Audiences are the Administrators audience plus one per committee; both are bounded and
            // enumerable, so we can sweep each in turn.
            var audiences = new List<string> { NotificationAudience.Administrators };
            var committees = await _committeeRepository.GetAllAsync();
            audiences.AddRange(committees.Select(c => NotificationAudience.Committee(c.Id)));

            // Sequential on purpose: the per-recipient throttle written while processing one audience
            // must be visible to the next, so a recipient in several audiences gets at most one digest
            // per min-interval rather than one per audience.
            foreach (var audience in audiences)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await ProcessAudienceAsync(audience, now, graceCutoff, minDigestInterval, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Failed to process escalation for audience {Audience}", audience);
                }
            }
        }

        private async Task ProcessAudienceAsync(
            string audience,
            DateTime now,
            DateTime graceCutoff,
            TimeSpan minDigestInterval,
            CancellationToken ct
        )
        {
            ct.ThrowIfCancellationRequested();

            // Oldest-first and already filtered to unresolved + un-escalated by the query, so the most
            // overdue items are never starved by a large backlog. Only the grace check remains in memory.
            var candidates = await _notificationRepository.GetUnescalatedByAudienceOldestFirstAsync(audience, 200);
            var aged = candidates.Where(n => n.CreatedUtc <= graceCutoff).ToList();
            if (aged.Count == 0)
                return;

            var emails = await _recipientResolver.ResolveAudienceEmailsAsync(audience, ct);
            if (emails.Count == 0)
            {
                // No one to email — leave the items un-escalated so they can be picked up once
                // recipients exist (e.g. an admin/committee member is added).
                _logger.LogWarning(
                    "{Count} aged notification(s) for audience {Audience} but no recipients resolved",
                    aged.Count,
                    audience
                );
                return;
            }

            // Throttle is per recipient, but a notification has a single escalated flag, so we send and
            // stamp as a unit: a digest goes out only when at least one recipient is past the min
            // interval, and when it does it goes to ALL of the audience's current recipients and stamps
            // the items once. Sending to only the "due" subset and stamping would permanently drop those
            // items from the email of any recipient that happened to be throttled on that one sweep
            // (they would still see them in-app, but never by email). Recording the send for every
            // recipient keeps the next digest at least a min-interval away.
            var states = await Task.WhenAll(emails.Select(e => _digestStateRepository.GetAsync(e)));
            var anyDue = states.Any(s => s == null || now - s.LastDigestUtc >= minDigestInterval);
            if (!anyDue)
            {
                // Everyone was recently digested; leave the aged items for a later sweep.
                return;
            }

            var recipients = emails.ToList();

            // Persist the job (Queued) but do NOT enqueue yet. Stamp the notifications and record the
            // digest send first, then enqueue last. Ordering matters for the "emailed at most once"
            // guarantee under a crash: if we die after stamping but before enqueue, the stamp is durable
            // so the next sweep won't re-escalate, and the persisted Queued job is picked back up by
            // EmailJobProcessor.ResumeIncompleteJobsAsync (startup + stall watchdog) and sent. Enqueueing
            // first (the previous order) could send the digest and then crash before stamping, causing a
            // duplicate digest on the next sweep.
            var job = await PersistDigestJobAsync(aged, recipients, now);

            // Stamp the aged notifications so they are not escalated again, and record the digest send
            // per recipient. These target independent documents, so run them together.
            await Task.WhenAll(
                StampEscalatedAsync(aged, job.Id, now),
                Task.WhenAll(
                    recipients.Select(e =>
                        _digestStateRepository.UpsertAsync(new NotificationDigestState { RecipientEmail = e, LastDigestUtc = now })
                    )
                )
            );

            // Enqueue last. A failure here (e.g. shutdown) leaves the job Queued for recovery rather
            // than re-sending — the notifications are already stamped, so it is never re-escalated.
            await _emailJobQueue.EnqueueAsync(job.Id, ct);

            _logger.LogInformation(
                "Escalated {ItemCount} notification(s) for audience {Audience} to {RecipientCount} recipient(s) via job {JobId}",
                aged.Count,
                audience,
                recipients.Count,
                job.Id
            );
        }

        /// <summary>
        /// Builds the digest <see cref="EmailJob"/>, uploads its HTML body, and persists it as Queued.
        /// Does not enqueue — the caller enqueues after stamping the notifications (see the ordering note
        /// in <see cref="ProcessAudienceAsync"/>).
        /// </summary>
        private async Task<EmailJob> PersistDigestJobAsync(
            List<Notification> aged,
            List<string> recipientEmails,
            DateTime now
        )
        {
            var recipients = recipientEmails
                .Select(e => new EmailJobRecipient
                {
                    Email = e,
                    // Guid.Empty suppresses unsubscribe headers/footer — this is a transactional alert.
                    HomeId = Guid.Empty,
                    Status = EmailJobRecipientStatus.Pending,
                })
                .ToList();

            var htmlBody = BuildDigestHtml(aged);

            var job = new EmailJob
            {
                Id = Guid.NewGuid(),
                Status = EmailJobStatus.Queued,
                Category = EscalationCategory,
                FromEmail = "webservice@cohad.org",
                FromDisplay = "COHAD Web",
                Subject = $"COHAD: {aged.Count} item(s) need attention",
                CreatedUtc = now,
                CreatedByUserId = "system:notification-escalation",
                CreatedByDisplayName = "Notification Escalation",
                MaxRecipientAttempts = 3,
                TotalRecipients = recipients.Count,
                Recipients = recipients,
                GroupRecipients = true,
            };

            job.ContentBlobPath = $"email-jobs/{job.Id:D}.html";
            var jobPersisted = false;
            try
            {
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(htmlBody)))
                {
                    await _fileStore.UploadAsync(job.ContentBlobPath, stream, "text/html");
                }

                await _emailJobRepository.AddAsync(job);
                jobPersisted = true;
            }
            catch
            {
                if (!jobPersisted && !string.IsNullOrEmpty(job.ContentBlobPath))
                {
                    try
                    {
                        await _fileStore.DeleteAsync(job.ContentBlobPath);
                    }
                    catch
                    { /* best-effort cleanup */
                    }
                }

                throw;
            }

            return job;
        }

        private async Task StampEscalatedAsync(List<Notification> aged, Guid jobId, DateTime now)
        {
            // Re-read each item before stamping so a human resolve that landed between the sweep's read
            // and this write is not clobbered (UpsertAsync is last-write-wins). Also skip anything
            // already escalated by a racing sweep.
            await Task.WhenAll(
                aged.Select(async n =>
                {
                    var fresh = await _notificationRepository.GetByIdAsync(n.Id);
                    if (fresh == null || fresh.ResolvedUtc != null || fresh.EscalatedUtc != null)
                        return;

                    fresh.EscalatedUtc = now;
                    fresh.EscalationJobId = jobId;
                    await _notificationRepository.UpsertAsync(fresh);
                })
            );
        }

        private string BuildDigestHtml(List<Notification> aged)
        {
            var cap = Math.Max(1, _options.MaxItemsPerDigest);
            var shown = aged.Take(cap).ToList();
            var overflow = aged.Count - shown.Count;

            var sb = new StringBuilder();
            sb.Append("<p>The following item(s) in COHAD have been waiting and may need your attention:</p>");
            sb.Append("<ul>");
            foreach (var n in shown)
            {
                var title = WebUtility.HtmlEncode(n.Title ?? string.Empty);
                var summary = WebUtility.HtmlEncode(n.Summary ?? string.Empty);
                sb.Append("<li><strong>").Append(title).Append("</strong>");
                if (!string.IsNullOrWhiteSpace(summary))
                    sb.Append(" — ").Append(summary);
                sb.Append("</li>");
            }
            sb.Append("</ul>");

            if (overflow > 0)
                sb.Append("<p>…and ").Append(overflow).Append(" more.</p>");

            sb.Append("<p>Sign in to COHAD to review them.</p>");
            return sb.ToString();
        }
    }
}
