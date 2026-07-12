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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Web.Models;
using Web.Services.Repositories;

namespace Web.Services
{
    /// <summary>
    /// Performs a single escalation sweep: for each audience, finds unresolved notifications that have
    /// aged past the grace period without being escalated, batches them into one digest email per due
    /// recipient (throttled by <see cref="NotificationEscalationOptions.MinDigestIntervalHours"/>, capped
    /// at <see cref="NotificationEscalationOptions.MaxItemsPerDigest"/> items), and stamps as escalated
    /// only the items actually emailed, so each is emailed at most once and any beyond the cap roll to a
    /// later sweep rather than being dropped from the email channel.
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
        private readonly string _appBaseUrl;
        private readonly ILogger<NotificationEscalationRunner> _logger;

        private const string EscalationCategory = "notification-escalation";

        /// <summary>Max concurrent escalation stamps (point read + conditional upsert) per sweep.</summary>
        private const int MaxStampConcurrency = 16;

        public NotificationEscalationRunner(
            INotificationRepository notificationRepository,
            INotificationRecipientResolver recipientResolver,
            ICommitteeRepository committeeRepository,
            INotificationDigestStateRepository digestStateRepository,
            IEmailJobRepository emailJobRepository,
            IDocumentFileStore fileStore,
            EmailJobQueue emailJobQueue,
            IOptions<NotificationEscalationOptions> options,
            IConfiguration config,
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
            // Same source/normalization as EmailJobProcessor so digest deep links match unsubscribe links.
            _appBaseUrl = (config["AppBaseUrl"] ?? string.Empty).TrimEnd('/');
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
            var throttled = new List<RecipientDigest>();
            for (var i = 0; i < buckets.Count; i++)
            {
                var state = states[i];
                if (state == null || now - state.LastDigestUtc >= minDigestInterval)
                    due.Add(buckets[i]);
                else
                    throttled.Add(buckets[i]);
            }

            if (due.Count == 0)
                return;

            // Each due recipient emails at most MaxItemsPerDigest items this sweep (oldest first); the
            // rest are deferred. Compute that "shown" set per recipient now, because only items that are
            // actually rendered and sent get stamped — an item left un-stamped stays eligible for a later
            // sweep instead of being stamped-and-dropped from the email channel.
            var cap = Math.Max(1, _options.MaxItemsPerDigest);
            foreach (var bucket in due)
            {
                bucket.JobId = Guid.NewGuid();
                bucket.Distinct = bucket.Items
                    .GroupBy(n => n.Id)
                    .Select(g => g.First())
                    .OrderBy(n => n.CreatedUtc)
                    .ToList();
                bucket.Shown = bucket.Distinct.Take(cap).ToList();
            }

            // Stamp the union of shown items FIRST (before any job is persisted), so the "emailed at most
            // once" guarantee holds under a crash: a persisted Queued job is sendable via
            // EmailJobProcessor.ResumeIncompleteJobsAsync, so the escalation stamp must be durable before
            // any job exists. Each item is stamped once, with a representative job id (the first due
            // recipient that shows it); an item that fails to stamp (resolved or escalated concurrently, or
            // a transient error) is simply left for a later sweep. A crash after stamping but before a job
            // is persisted drops that one digest — acceptable, the in-app channel is durable.
            var itemToRepJob = new Dictionary<Guid, (Notification Item, Guid JobId)>();
            foreach (var bucket in due)
                foreach (var item in bucket.Shown)
                    if (!itemToRepJob.ContainsKey(item.Id))
                        itemToRepJob[item.Id] = (item, bucket.JobId);

            // Bound the concurrent stamping: a large backlog across many committees could otherwise fan
            // out into thousands of simultaneous Cosmos point-read + conditional-upsert pairs, spiking RU
            // and risking 429s. Cap it so the sweep stays steady.
            using var stampGate = new SemaphoreSlim(MaxStampConcurrency);
            var stampResults = await Task.WhenAll(
                itemToRepJob.Values.Select(async pair =>
                {
                    await stampGate.WaitAsync(ct);
                    try
                    {
                        var stamped = await StampOneEscalatedAsync(pair.Item.Id, pair.JobId, now);
                        return (pair.Item.Id, Stamped: stamped != null);
                    }
                    finally
                    {
                        stampGate.Release();
                    }
                })
            );
            var stampedIds = new HashSet<Guid>();
            foreach (var (id, stamped) in stampResults)
                if (stamped)
                    stampedIds.Add(id);

            // Observability for the residual cross-recipient throttle drop: escalation is stamped once
            // globally per item, so an item owed to a throttled recipient but stamped this sweep on behalf
            // of a due recipient will never reach the throttled recipient by email (an escalated item is
            // not resurfaced in a later sweep). Benign - the in-app channel is durable and any co-recipient
            // can act - but log it so a report of a missed email is diagnosable. Fully removing the drop
            // needs per-recipient escalation state (tracked in issue #248).
            foreach (var bucket in throttled)
            {
                var droppedIds = bucket.Items
                    .Select(n => n.Id)
                    .Distinct()
                    .Where(stampedIds.Contains)
                    .ToList();
                if (droppedIds.Count > 0)
                    _logger.LogWarning(
                        "Throttled recipient {Recipient} was owed {Count} item(s) escalated to other recipients this sweep; they will not be emailed to this recipient (in-app only): {ItemIds}",
                        bucket.Email,
                        droppedIds.Count,
                        string.Join(", ", droppedIds)
                    );
            }

            // Track which stamped items actually got queued for email, so any stamped item emailed to no
            // one (a transient send failure after the at-most-once stamp) is observable below.
            var emailedIds = new HashSet<Guid>();

            // Send each due recipient one combined digest of their stamped items, then record the send.
            foreach (var bucket in due)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    // bucket.Shown is already de-duplicated and oldest-first; keep only what actually stamped.
                    var items = bucket.Shown
                        .Where(n => stampedIds.Contains(n.Id))
                        .ToList();
                    if (items.Count == 0)
                        continue; // all of this recipient's shown items were resolved/stamped-away or failed to stamp.

                    // Overflow items (beyond the cap) that no digest stamped this sweep stay eligible for
                    // this recipient's next sweep. Count only those, computed against the actual stamps, so
                    // the follow-up note is truthful: an overflow item stamped for another due recipient is
                    // now escalated and will not reach this recipient again, so it must not be counted.
                    var deferredCount = bucket.Distinct.Skip(cap).Count(n => !stampedIds.Contains(n.Id));
                    var (job, htmlBody) = BuildDigestJob(bucket.JobId, items, deferredCount, new List<string> { bucket.Email }, now);
                    await PersistAndEnqueueJobAsync(job, htmlBody, ct);

                    // The job is durably Queued now (recoverable via ResumeIncompleteJobsAsync), so these
                    // items are effectively emailed even if the throttle-state upsert below fails.
                    foreach (var n in items)
                        emailedIds.Add(n.Id);

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

            // Any item stamped escalated but queued for no recipient this sweep (a transient send failure
            // after the at-most-once stamp) is dropped from the email channel - it stays escalated, so it
            // won't resurface. Benign (durable in-app), but log it so the drop is observable rather than
            // silent. See the stamp-before-persist tradeoff above and issue #248.
            var orphanedIds = stampedIds.Where(id => !emailedIds.Contains(id)).ToList();
            if (orphanedIds.Count > 0)
                _logger.LogWarning(
                    "{Count} notification(s) were stamped escalated but not queued to any recipient this sweep (send failure after stamping); they will not be emailed: {ItemIds}",
                    orphanedIds.Count,
                    string.Join(", ", orphanedIds)
                );
        }

        private sealed class RecipientDigest
        {
            public RecipientDigest(string email) => Email = email;

            public string Email { get; }

            public List<Notification> Items { get; } = new();

            public Guid JobId { get; set; }

            /// <summary>All of this recipient's aged items, de-duplicated and oldest-first.</summary>
            public List<Notification> Distinct { get; set; } = new();

            /// <summary>The oldest-first prefix actually emailed this sweep (capped at
            /// <see cref="NotificationEscalationOptions.MaxItemsPerDigest"/>). Only these are stamped.</summary>
            public List<Notification> Shown { get; set; } = new();
        }

        /// <summary>
        /// Builds the digest <see cref="EmailJob"/> (using the already-assigned <paramref name="jobId"/>)
        /// and its HTML body in memory from the <paramref name="items"/> that were actually stamped for
        /// this job. <paramref name="deferredCount"/> is the number of this recipient's aged items left
        /// un-stamped this sweep and thus still eligible for their next digest. Does not persist or enqueue.
        /// </summary>
        private (EmailJob Job, string HtmlBody) BuildDigestJob(
            Guid jobId,
            List<Notification> items,
            int deferredCount,
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

            return (job, BuildDigestHtml(items, deferredCount));
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

        private string BuildDigestHtml(List<Notification> shown, int deferredCount)
        {
            var sb = new StringBuilder();
            sb.Append("<p>The following item(s) in COHAD have been waiting and may need your attention:</p>");
            sb.Append("<ul>");
            foreach (var n in shown)
            {
                var title = WebUtility.HtmlEncode(n.Title ?? string.Empty);
                var summary = WebUtility.HtmlEncode(n.Summary ?? string.Empty);
                var link = BuildDeepLink(n.DeepLink);
                sb.Append("<li>");
                // Deep link straight to the item's moderation page when we can build an absolute URL;
                // otherwise fall back to a plain bold title.
                if (link != null)
                    sb.Append("<a href=\"").Append(WebUtility.HtmlEncode(link)).Append("\"><strong>").Append(title).Append("</strong></a>");
                else
                    sb.Append("<strong>").Append(title).Append("</strong>");
                if (!string.IsNullOrWhiteSpace(summary))
                    sb.Append(" — ").Append(summary);
                sb.Append("</li>");
            }
            sb.Append("</ul>");

            if (deferredCount > 0)
                sb.Append("<p>…and ").Append(deferredCount).Append(" more waiting; you'll receive them in a follow-up.</p>");

            if (!string.IsNullOrEmpty(_appBaseUrl))
                sb.Append("<p><a href=\"").Append(WebUtility.HtmlEncode(_appBaseUrl)).Append("\">Sign in to COHAD</a> to review them.</p>");
            else
                sb.Append("<p>Sign in to COHAD to review them.</p>");
            return sb.ToString();
        }

        /// <summary>
        /// Turns a notification's root-relative <see cref="Notification.DeepLink"/> (e.g.
        /// <c>/manage/approvals?message=...</c>) into an absolute URL under <see cref="_appBaseUrl"/>.
        /// Returns null when no base URL is configured or the notification has no deep link, so the
        /// digest degrades to a plain title (matching the pre-deep-link behavior).
        /// </summary>
        private string? BuildDeepLink(string? relative)
        {
            if (string.IsNullOrEmpty(_appBaseUrl) || string.IsNullOrWhiteSpace(relative))
                return null;
            return _appBaseUrl + (relative.StartsWith('/') ? relative : "/" + relative);
        }
    }
}
