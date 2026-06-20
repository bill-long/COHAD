#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
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

            // Sequential on purpose, and we also track who was emailed in this sweep in memory: a
            // recipient that belongs to several audiences gets at most one digest per sweep even if the
            // persisted per-recipient throttle write for an earlier audience failed (the per-audience
            // catch below keeps the sweep going on failure). The in-memory set is the authoritative
            // cross-audience guard within a sweep; the persisted digest state enforces the throttle
            // across sweeps.
            var emailedThisSweep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var audience in audiences)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await ProcessAudienceAsync(audience, now, graceCutoff, minDigestInterval, emailedThisSweep, ct);
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
            HashSet<string> emailedThisSweep,
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

            // Exclude recipients already emailed earlier in this sweep so a recipient in several audiences
            // is not digested twice in one run. If that leaves no one, defer the aged items to a later
            // sweep rather than stamping them (so the excluded recipients still get them by email later).
            var recipients = emails.Where(e => !emailedThisSweep.Contains(e)).ToList();
            if (recipients.Count == 0)
                return;

            // Throttle is per recipient, but a notification has a single escalated flag, so we send and
            // stamp as a unit: a digest goes out only when at least one remaining recipient is past the
            // min interval, and when it does it goes to ALL of those recipients and stamps the items once.
            // Sending to only the "due" subset and stamping would permanently drop those items from the
            // email of any recipient that happened to be throttled on that one sweep (they would still see
            // them in-app, but never by email).
            var states = await Task.WhenAll(recipients.Select(e => _digestStateRepository.GetAsync(e)));
            var anyDue = states.Any(s => s == null || now - s.LastDigestUtc >= minDigestInterval);
            if (!anyDue)
            {
                // Everyone still eligible was recently digested; leave the aged items for a later sweep.
                return;
            }

            // The writes below are deliberately ordered:
            //   1. Stamp the aged notifications escalated, collecting the subset actually stamped for this
            //      job's id. Stamping must be durable BEFORE the job exists: once a job is persisted Queued
            //      it is sendable (EmailJobProcessor.ResumeIncompleteJobsAsync re-enqueues Queued jobs), so
            //      stamping first is what makes the "emailed at most once" guarantee hold under a crash.
            //      The cost is that a crash after stamping but before the job is persisted drops that one
            //      digest — acceptable because the in-app notification is the durable channel.
            //   2. Build the digest from the stamped subset only, so the email never lists an item that was
            //      resolved or escalated by someone else between the query and the stamp.
            //   3. Persist + enqueue the job.
            //   4. Mark these recipients as emailed in this sweep, then record the per-recipient digest
            //      send. The in-memory mark happens before the persisted write so the cross-audience guard
            //      holds even if the digest-state write fails; the persisted write is the cross-sweep
            //      throttle and comes LAST so a failure above never throttles recipients for a digest that
            //      was not actually queued.
            var jobId = Guid.NewGuid();
            var stamped = await StampEscalatedAsync(aged, jobId, now);
            if (stamped.Count == 0)
            {
                // Everything aged here was resolved or escalated by someone else first — nothing to send,
                // and we must not throttle recipients for a digest we never queued.
                return;
            }

            var (job, htmlBody) = BuildDigestJob(jobId, stamped, recipients, now);

            await PersistAndEnqueueJobAsync(job, htmlBody, ct);

            foreach (var e in recipients)
                emailedThisSweep.Add(e);

            await Task.WhenAll(
                recipients.Select(e =>
                    _digestStateRepository.UpsertAsync(new NotificationDigestState { RecipientEmail = e, LastDigestUtc = now })
                )
            );

            _logger.LogInformation(
                "Escalated {ItemCount} notification(s) for audience {Audience} to {RecipientCount} recipient(s) via job {JobId}",
                stamped.Count,
                audience,
                recipients.Count,
                job.Id
            );
        }

        /// <summary>
        /// Builds the digest <see cref="EmailJob"/> (using the already-assigned <paramref name="jobId"/>)
        /// and its HTML body in memory from the <paramref name="items"/> that were actually stamped for
        /// this job. Does not persist or enqueue.
        /// </summary>
        private (EmailJob Job, string HtmlBody) BuildDigestJob(
            Guid jobId,
            List<Notification> items,
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

            var job = new EmailJob
            {
                Id = jobId,
                Status = EmailJobStatus.Queued,
                Category = EscalationCategory,
                FromEmail = "webservice@cohad.org",
                FromDisplay = "COHAD Web",
                Subject = $"COHAD: {items.Count} item(s) need attention",
                CreatedUtc = now,
                CreatedByUserId = "system:notification-escalation",
                CreatedByDisplayName = "Notification Escalation",
                MaxRecipientAttempts = 3,
                TotalRecipients = recipients.Count,
                Recipients = recipients,
                GroupRecipients = true,
                ContentBlobPath = $"email-jobs/{jobId:D}.html",
            };

            return (job, BuildDigestHtml(items));
        }

        /// <summary>Uploads the digest body, persists the job as Queued, and enqueues it for the processor.</summary>
        private async Task PersistAndEnqueueJobAsync(EmailJob job, string htmlBody, CancellationToken ct)
        {
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

            // Enqueue last. A failure here (e.g. shutdown) leaves the job Queued for recovery by
            // EmailJobProcessor.ResumeIncompleteJobsAsync rather than losing it.
            await _emailJobQueue.EnqueueAsync(job.Id, ct);
        }

        /// <summary>
        /// Stamps each aged notification escalated and returns the subset that was actually stamped, so
        /// the digest can be built from exactly the items now bound to this job.
        /// </summary>
        private async Task<List<Notification>> StampEscalatedAsync(List<Notification> aged, Guid jobId, DateTime now)
        {
            var results = await Task.WhenAll(aged.Select(n => StampOneEscalatedAsync(n.Id, jobId, now)));
            return results.Where(r => r != null).Select(r => r!).ToList();
        }

        /// <summary>
        /// Stamps a single notification escalated under optimistic concurrency: re-read, set the
        /// escalation fields, and write with an ETag precondition. Returns the stamped notification, or
        /// null if it was skipped (already resolved/escalated, or repeatedly lost the race). If a
        /// concurrent write (typically a human resolve) lands first, the conditional write fails and we
        /// re-read — skipping the stamp if the item is now resolved/escalated — so we never clobber
        /// <c>ResolvedUtc</c> or re-email a resolved item.
        /// </summary>
        private async Task<Notification?> StampOneEscalatedAsync(Guid id, Guid jobId, DateTime now)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var fresh = await _notificationRepository.GetByIdAsync(id);
                if (fresh == null || fresh.ResolvedUtc != null || fresh.EscalatedUtc != null)
                    return null;

                fresh.EscalatedUtc = now;
                fresh.EscalationJobId = jobId;
                try
                {
                    await _notificationRepository.UpsertWithEtagAsync(fresh);
                    return fresh;
                }
                catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
                {
                    // Lost the race; loop to re-read and re-evaluate before stamping.
                }
            }

            _logger.LogWarning("Gave up stamping notification {Id} escalated after a concurrent update", id);
            return null;
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
