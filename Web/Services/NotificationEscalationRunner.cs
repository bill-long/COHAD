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

            // The sweep is recipient-centric, not audience-centric: a recipient (e.g. an Administrator,
            // who can act on every committee audience) gets ONE combined digest per sweep covering all
            // their aged items across every audience they belong to. This is what lets us throttle to a
            // single email per recipient per sweep without dropping any audience's items from that
            // recipient's email.
            var audiences = new List<string> { NotificationAudience.Administrators };
            var committees = await _committeeRepository.GetAllAsync();
            audiences.AddRange(committees.Select(c => NotificationAudience.Committee(c.Id)));

            // Gather aged items per recipient (case-insensitive on email, preserving the first casing).
            var byRecipient = new Dictionary<string, RecipientDigest>(StringComparer.OrdinalIgnoreCase);
            foreach (var audience in audiences)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var candidates = await _notificationRepository.GetUnescalatedByAudienceOldestFirstAsync(audience, 200);
                    var aged = candidates.Where(n => n.CreatedUtc <= graceCutoff).ToList();
                    if (aged.Count == 0)
                        continue;

                    var emails = await _recipientResolver.ResolveAudienceEmailsAsync(audience, ct);
                    if (emails.Count == 0)
                    {
                        // No one to email — leave the items un-escalated so they can be picked up once
                        // recipients exist (e.g. a committee moderator is added).
                        _logger.LogWarning(
                            "{Count} aged notification(s) for audience {Audience} but no recipients resolved",
                            aged.Count,
                            audience
                        );
                        continue;
                    }

                    foreach (var email in emails)
                    {
                        if (!byRecipient.TryGetValue(email, out var bucket))
                        {
                            bucket = new RecipientDigest(email);
                            byRecipient[email] = bucket;
                        }
                        bucket.Items.AddRange(aged);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Failed to gather escalations for audience {Audience}", audience);
                }
            }

            if (byRecipient.Count == 0)
                return;

            // Decide which recipients are due (per-recipient throttle, across sweeps via the persisted
            // digest state). The reads are independent, so fetch them in parallel.
            ct.ThrowIfCancellationRequested();
            var buckets = byRecipient.Values.ToList();
            var states = await Task.WhenAll(buckets.Select(b => _digestStateRepository.GetAsync(b.Email)));
            var due = new List<RecipientDigest>();
            for (var i = 0; i < buckets.Count; i++)
            {
                var state = states[i];
                if (state == null || now - state.LastDigestUtc >= minDigestInterval)
                    due.Add(buckets[i]);
            }

            if (due.Count == 0)
                return;

            // Stamp the union of items going to at least one due recipient FIRST (before any job is
            // persisted), so the "emailed at most once" guarantee holds under a crash: a persisted Queued
            // job is sendable via EmailJobProcessor.ResumeIncompleteJobsAsync, so the escalation stamp must
            // be durable before any job exists. Each item is stamped once, with a representative job id
            // (the first due recipient that carries it); an item that fails to stamp (resolved or escalated
            // concurrently, or a transient error) is simply left for a later sweep. A crash after stamping
            // but before a job is persisted drops that one digest — acceptable, the in-app channel is
            // durable.
            var itemToRepJob = new Dictionary<Guid, (Notification Item, Guid JobId)>();
            foreach (var bucket in due)
            {
                bucket.JobId = Guid.NewGuid();
                foreach (var item in bucket.Items)
                    if (!itemToRepJob.ContainsKey(item.Id))
                        itemToRepJob[item.Id] = (item, bucket.JobId);
            }

            var stampedIds = new HashSet<Guid>();
            var stampResults = await Task.WhenAll(
                itemToRepJob.Values.Select(async pair =>
                {
                    var stamped = await StampOneEscalatedAsync(pair.Item.Id, pair.JobId, now);
                    return (pair.Item.Id, Stamped: stamped != null);
                })
            );
            foreach (var (id, stamped) in stampResults)
                if (stamped)
                    stampedIds.Add(id);

            // Send each due recipient one combined digest of their stamped items, then record the send.
            foreach (var bucket in due)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var items = bucket.Items
                        .Where(n => stampedIds.Contains(n.Id))
                        .GroupBy(n => n.Id)
                        .Select(g => g.First())
                        .OrderBy(n => n.CreatedUtc)
                        .ToList();
                    if (items.Count == 0)
                        continue; // all of this recipient's items were resolved/stamped-away or failed to stamp.

                    var (job, htmlBody) = BuildDigestJob(bucket.JobId, items, new List<string> { bucket.Email }, now);
                    await PersistAndEnqueueJobAsync(job, htmlBody, ct);

                    // Record the send LAST so a failure above never throttles a recipient for a digest that
                    // was not actually queued.
                    await _digestStateRepository.UpsertAsync(new NotificationDigestState { RecipientEmail = bucket.Email, LastDigestUtc = now });

                    _logger.LogInformation(
                        "Escalated {ItemCount} notification(s) to {Recipient} via job {JobId}",
                        items.Count,
                        bucket.Email,
                        job.Id
                    );
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Failed to send escalation digest to {Recipient}", bucket.Email);
                }
            }
        }

        private sealed class RecipientDigest
        {
            public RecipientDigest(string email) => Email = email;

            public string Email { get; }

            public List<Notification> Items { get; } = new();

            public Guid JobId { get; set; }
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
        /// Stamps a single notification escalated under optimistic concurrency: re-read, set the
        /// escalation fields, and write with an ETag precondition. Returns the stamped notification, or
        /// null if it was skipped (already resolved/escalated, or repeatedly lost the race). If a
        /// concurrent write (typically a human resolve) lands first, the conditional write fails and we
        /// re-read — skipping the stamp if the item is now resolved/escalated — so we never clobber
        /// <c>ResolvedUtc</c> or re-email a resolved item.
        /// </summary>
        private async Task<Notification?> StampOneEscalatedAsync(Guid id, Guid jobId, DateTime now)
        {
            try
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
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A transient failure stamping one item must not abort Task.WhenAll for the whole audience
                // (which would orphan items other tasks already stamped, leaving them escalated with no
                // job). Leave this one un-escalated so a later sweep retries it.
                _logger.LogWarning(ex, "Failed to stamp notification {Id} escalated; leaving it for a later sweep", id);
                return null;
            }
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
