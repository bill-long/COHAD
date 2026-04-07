using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MimeKit;
using Web.Hubs;
using Web.Models;
using Web.Services.Repositories;

namespace Web.Services
{
    /// <summary>
    /// Background service that processes queued email jobs. On startup, resumes any incomplete
    /// jobs found in the database. Sends one email per recipient with per-recipient error handling,
    /// persisting progress after each send so the job can be resumed if the process crashes.
    /// </summary>
    public sealed class EmailJobProcessor : BackgroundService
    {
        private readonly EmailJobQueue _queue;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IUnsubscribeTokenService _tokenService;
        private readonly EmailTransportRouter _transportRouter;
        private readonly IHubContext<EmailJobHub> _hubContext;
        private readonly ILogger<EmailJobProcessor> _logger;
        private readonly string _appBaseUrl;
        private readonly bool _isMockMode;
        private readonly bool _emailJobsEnabled;
        private readonly int _defaultMaxRecipientAttempts;
        private readonly int _stallAfterMinutes;

        /// <summary>MockData only: per-recipient delay before marking Sent/Failed.</summary>
        private readonly int _mockDelayMilliseconds;

        /// <summary>MockData only: probability each recipient fails (0–1), independent rolls.</summary>
        private readonly double _mockRandomFailureProbability;

        /// <summary>MockData only: when true, every simulated send fails.</summary>
        private readonly bool _mockFailAllRecipients;

        /// <summary>MockData only: when set, the job fails immediately with this message (no per-recipient processing).</summary>
        private readonly string _mockJobFatalError;

        private readonly Random _mockRandom;

        // Tracks in-memory cancellation tokens so the cancel API can stop a running job.
        private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeJobs = new();

        /// <summary>
        /// Derives SentCount and FailedCount from the actual recipient statuses,
        /// avoiding drift during retries of previously-failed recipients.
        /// </summary>
        private static void RecalculateCounts(EmailJob job)
        {
            var recipients = job.Recipients ?? new();
            job.SentCount = recipients.Count(r => r.Status == EmailJobRecipientStatus.Sent);
            job.FailedCount = recipients.Count(r => r.Status == EmailJobRecipientStatus.Failed);
        }

        /// <summary>
        /// Promotes recipients that are still Pending or Failed but have a non-Unknown
        /// DeliveryStatus to Sent, since a delivery event from the provider proves the
        /// email was sent. This normalizes state before filtering so the processor does
        /// not re-send them.
        /// </summary>
        private static bool NormalizePendingDelivered(EmailJob job)
        {
            var changed = false;
            foreach (var r in job.Recipients ?? new())
            {
                if (
                    (r.Status == EmailJobRecipientStatus.Pending || r.Status == EmailJobRecipientStatus.Failed)
                    && r.DeliveryStatus != DeliveryStatus.Unknown
                )
                {
                    r.Status = EmailJobRecipientStatus.Sent;
                    r.SentUtc ??= r.DeliveryStatusUpdatedUtc ?? DateTime.UtcNow;
                    r.Error = null;
                    changed = true;
                }
            }

            if (changed)
                RecalculateCounts(job);

            return changed;
        }

        /// <summary>
        /// Recipients still Pending/Failed but at or over the attempt cap cannot be sent; mark them Failed
        /// so terminal status reflects reality. Preserves any existing per-recipient error text.
        /// </summary>
        private static void MarkCappedRecipientsAsFailed(EmailJob job)
        {
            var capMessage = $"Max attempts reached ({job.MaxRecipientAttempts}).";
            foreach (var r in job.Recipients ?? new List<EmailJobRecipient>())
            {
                if (
                    (r.Status == EmailJobRecipientStatus.Pending || r.Status == EmailJobRecipientStatus.Failed)
                    && r.AttemptCount >= job.MaxRecipientAttempts
                )
                {
                    // If the webhook already confirmed delivery, honour that rather than marking Failed.
                    if (r.DeliveryStatus != DeliveryStatus.Unknown)
                    {
                        r.Status = EmailJobRecipientStatus.Sent;
                        r.SentUtc ??= r.DeliveryStatusUpdatedUtc ?? DateTime.UtcNow;
                        r.Error = null;
                        continue;
                    }

                    r.Status = EmailJobRecipientStatus.Failed;
                    if (string.IsNullOrWhiteSpace(r.Error))
                        r.Error = capMessage;
                    else if (!r.Error.Contains(capMessage, StringComparison.OrdinalIgnoreCase))
                        r.Error = $"{r.Error} Additional info: {capMessage}";
                }
            }
        }

        /// <summary>
        /// Persists job state with optimistic concurrency (If-Match / ETag).
        /// On conflict, merges webhook-owned fields from the server copy (so delivery
        /// status updates are not lost) then retries up to <paramref name="maxAttempts"/> times.
        /// Returns false when the server copy is cancelled, missing, or contention persists.
        /// </summary>
        private static async Task<bool> TryPersistJobAsync(IEmailJobRepository repo, EmailJob job, int maxAttempts = 3)
        {
            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                try
                {
                    await repo.UpdateAsync(job);
                    return true;
                }
                catch (EmailJobConcurrencyException)
                {
                    var latest = await repo.GetByIdAsync(job.Id);
                    if (latest == null)
                        return false;
                    if (latest.Status == EmailJobStatus.Cancelled)
                        return false;
                    MergeWebhookFields(job, latest);
                    // Re-derive Status from the merged DeliveryStatus so the next write
                    // does not revert webhook self-healing (Pending/Failed → Sent).
                    NormalizePendingDelivered(job);
                    job.ETag = latest.ETag;
                }
            }

            return false;
        }

        /// <summary>
        /// Copies webhook-owned fields (DeliveryStatus, DeliveryStatusUpdatedUtc, ProviderMessageId)
        /// from the server copy into the processor's in-memory job so the next write does not
        /// overwrite changes made by the webhook controller.
        /// </summary>
        internal static void MergeWebhookFields(EmailJob local, EmailJob server)
        {
            if (local.Recipients == null || server.Recipients == null)
                return;

            var serverLookup = new Dictionary<string, EmailJobRecipient>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in server.Recipients)
            {
                if (!string.IsNullOrEmpty(r.Email))
                    serverLookup[r.Email] = r;
            }

            foreach (var localRecipient in local.Recipients)
            {
                if (string.IsNullOrEmpty(localRecipient.Email))
                    continue;

                if (!serverLookup.TryGetValue(localRecipient.Email, out var serverRecipient))
                    continue;

                // Adopt the server's delivery status when it is non-Unknown (webhook-owned field).
                // The processor never writes delivery status, so the server copy is authoritative.
                if (serverRecipient.DeliveryStatus != DeliveryStatus.Unknown)
                {
                    localRecipient.DeliveryStatus = serverRecipient.DeliveryStatus;
                    localRecipient.DeliveryStatusUpdatedUtc = serverRecipient.DeliveryStatusUpdatedUtc;
                }

                if (
                    !string.IsNullOrEmpty(serverRecipient.ProviderMessageId)
                    && string.IsNullOrEmpty(localRecipient.ProviderMessageId)
                )
                {
                    localRecipient.ProviderMessageId = serverRecipient.ProviderMessageId;
                }
            }
        }

        public EmailJobProcessor(
            EmailJobQueue queue,
            IServiceScopeFactory scopeFactory,
            IUnsubscribeTokenService tokenService,
            EmailTransportRouter transportRouter,
            IHubContext<EmailJobHub> hubContext,
            IConfiguration config,
            IWebHostEnvironment env,
            ILogger<EmailJobProcessor> logger
        )
        {
            _queue = queue;
            _scopeFactory = scopeFactory;
            _tokenService = tokenService;
            _transportRouter = transportRouter;
            _hubContext = hubContext;
            _logger = logger;
            _appBaseUrl = (config["AppBaseUrl"] ?? "").TrimEnd('/');
            _isMockMode = env.IsEnvironment("MockData");
            _emailJobsEnabled = config.GetValue("EmailJobs:Enabled", true);
            _defaultMaxRecipientAttempts = config.GetValue("EmailJobs:DefaultMaxRecipientAttempts", 3);
            _stallAfterMinutes = config.GetValue("EmailJobs:StallAfterMinutes", 30);

            var mockDelayDefault = _isMockMode ? 250 : 50;
            _mockDelayMilliseconds = Math.Max(0, config.GetValue("EmailJobs:Mock:DelayMilliseconds", mockDelayDefault));
            _mockRandomFailureProbability = Math.Clamp(
                config.GetValue("EmailJobs:Mock:RandomFailureProbability", 0.0),
                0.0,
                1.0
            );
            _mockFailAllRecipients = config.GetValue("EmailJobs:Mock:FailAllRecipients", false);
            _mockJobFatalError = config["EmailJobs:Mock:JobFatalError"] ?? "";
            var mockSeed = config.GetValue<int?>("EmailJobs:Mock:RandomFailSeed");
            _mockRandom = mockSeed.HasValue ? new Random(mockSeed.Value) : new Random();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_emailJobsEnabled)
            {
                _logger.LogInformation("Email job processor disabled via EmailJobs:Enabled=false");
                return;
            }

            // Resume incomplete jobs from a previous run
            await ResumeIncompleteJobsAsync(stoppingToken);

            // Process new jobs as they arrive
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var jobId = await _queue.DequeueAsync(stoppingToken);
                    await ProcessJobAsync(jobId, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error in email job processing loop");
                    // Brief pause to avoid tight error loop
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }
        }

        private async Task ResumeIncompleteJobsAsync(CancellationToken ct)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<IEmailJobRepository>();
                var incompleteJobs = await repo.GetIncompleteJobsAsync();

                foreach (var job in incompleteJobs)
                {
                    if (ct.IsCancellationRequested)
                        break;

                    if (job.Status == EmailJobStatus.InProgress)
                    {
                        var thresholdMinutes = Math.Clamp(_stallAfterMinutes, 1, 24 * 60);
                        var lastProgress = job.LastProgressUtc ?? job.StartedUtc ?? job.CreatedUtc;
                        if (DateTime.UtcNow - lastProgress >= TimeSpan.FromMinutes(thresholdMinutes))
                        {
                            var recipients = job.Recipients ?? new List<EmailJobRecipient>();

                            // If there are genuinely unsent recipients (Pending/Failed with no
                            // delivery confirmation), re-enqueue rather than declaring the job
                            // terminal — the processor was interrupted, not permanently stuck.
                            var hasUnsentRecipients = recipients.Any(r =>
                                (
                                    r.Status == EmailJobRecipientStatus.Pending
                                    || r.Status == EmailJobRecipientStatus.Failed
                                )
                                && r.DeliveryStatus == DeliveryStatus.Unknown
                                && r.AttemptCount
                                    < (
                                        job.MaxRecipientAttempts > 0
                                            ? job.MaxRecipientAttempts
                                            : _defaultMaxRecipientAttempts
                                    )
                            );
                            if (hasUnsentRecipients)
                            {
                                _logger.LogWarning(
                                    "Email job {JobId} stalled since {LastProgressUtc} but has unsent recipients — re-queuing instead of terminating",
                                    job.Id,
                                    lastProgress
                                );
                                await _queue.EnqueueAsync(job.Id, ct);
                                continue;
                            }

                            NormalizePendingDelivered(job);
                            RecalculateCounts(job);
                            if (recipients.Count > 0 && recipients.All(r => r.Status == EmailJobRecipientStatus.Sent))
                            {
                                job.Status = EmailJobStatus.Completed;
                            }
                            else if (job.SentCount > 0)
                            {
                                job.Status = EmailJobStatus.PartiallyCompleted;
                            }
                            else
                            {
                                job.Status = EmailJobStatus.Failed;
                            }

                            job.LastError =
                                $"Job stalled (no progress since {lastProgress:u}). Stopping to prevent repeated retries.";
                            job.CompletedUtc = DateTime.UtcNow;

                            if (await TryPersistJobAsync(repo, job))
                            {
                                await NotifyCompletedAsync(job);
                                _logger.LogWarning(
                                    "Email job {JobId} stalled since {LastProgressUtc} — marked {Status} and will not resume",
                                    job.Id,
                                    lastProgress,
                                    job.Status
                                );
                                continue;
                            }

                            var latest = await repo.GetByIdAsync(job.Id);
                            if (latest == null || latest.Status == EmailJobStatus.Cancelled)
                            {
                                _logger.LogInformation(
                                    "Stall watchdog: job {JobId} missing or cancelled after failed persist — skipping",
                                    job.Id
                                );
                                continue;
                            }

                            if (latest.Status != EmailJobStatus.InProgress)
                            {
                                _logger.LogInformation(
                                    "Stall watchdog: job {JobId} is no longer InProgress ({Status}) after failed persist — skipping",
                                    job.Id,
                                    latest.Status
                                );
                                continue;
                            }

                            _logger.LogWarning(
                                "Email job {JobId} stalled since {LastProgressUtc} — failed to persist terminal status; enqueueing for normal processing",
                                job.Id,
                                lastProgress
                            );
                            await _queue.EnqueueAsync(job.Id, ct);
                            continue;
                        }
                    }

                    _logger.LogInformation(
                        "Resuming incomplete email job {JobId} (status={Status}, sent={Sent}/{Total})",
                        job.Id,
                        job.Status,
                        job.SentCount,
                        job.TotalRecipients
                    );
                    await _queue.EnqueueAsync(job.Id, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resume incomplete email jobs on startup");
            }
        }

        private async Task ProcessJobAsync(Guid jobId, CancellationToken appStopping)
        {
            using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(appStopping);
            _activeJobs[jobId] = jobCts;

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<IEmailJobRepository>();
                var fileStore = scope.ServiceProvider.GetRequiredService<IDocumentFileStore>();

                var job = await repo.GetByIdAsync(jobId);
                if (job == null)
                {
                    _logger.LogWarning("Email job {JobId} not found — skipping", jobId);
                    return;
                }

                if (job.Status == EmailJobStatus.Cancelled)
                {
                    _logger.LogInformation("Email job {JobId} was cancelled — skipping", jobId);
                    return;
                }

                if (job.Status != EmailJobStatus.Queued && job.Status != EmailJobStatus.InProgress)
                {
                    _logger.LogInformation("Email job {JobId} has status {Status} — skipping", jobId, job.Status);
                    return;
                }

                // Atomically claim the job using ETag-based optimistic concurrency.
                // If another instance already claimed it, the ETag will mismatch and TryClaimAsync returns false.
                job.Status = EmailJobStatus.InProgress;
                job.StartedUtc ??= DateTime.UtcNow;
                job.LastProgressUtc ??= job.StartedUtc;
                var effectiveMaxAttempts =
                    job.MaxRecipientAttempts > 0 ? job.MaxRecipientAttempts : _defaultMaxRecipientAttempts;
                job.MaxRecipientAttempts = Math.Clamp(effectiveMaxAttempts, 1, 25);
                if (!await repo.TryClaimAsync(job))
                {
                    _logger.LogInformation(
                        "Email job {JobId} was already claimed by another instance — skipping",
                        jobId
                    );
                    return;
                }

                // Load HTML content from blob storage
                string htmlBody;
                var blobResult = await fileStore.DownloadAsync(job.ContentBlobPath);
                if (blobResult == null)
                {
                    job.Status = EmailJobStatus.Failed;
                    job.LastError = "Email content not found in storage.";
                    job.CompletedUtc = DateTime.UtcNow;
                    if (!await TryPersistJobAsync(repo, job))
                    {
                        var latest = await repo.GetByIdAsync(jobId);
                        if (latest != null)
                            await NotifyCompletedAsync(latest);
                        return;
                    }

                    await NotifyCompletedAsync(job);
                    _logger.LogError(
                        "Email job {JobId} failed: content blob not found at {Path}",
                        jobId,
                        job.ContentBlobPath
                    );
                    return;
                }

                using (var reader = new StreamReader(blobResult.Stream))
                {
                    htmlBody = await reader.ReadToEndAsync(jobCts.Token);
                }

                if (_isMockMode)
                {
                    await ProcessJobMockAsync(job, repo, jobCts.Token);
                }
                else
                {
                    await ProcessJobSendAsync(job, htmlBody, repo, jobCts.Token);
                }
            }
            catch (OperationCanceledException)
                when (jobCts.IsCancellationRequested && !appStopping.IsCancellationRequested)
            {
                // Job was cancelled via the cancel API — the controller persists the Cancelled status
                _logger.LogInformation("Email job {JobId} cancelled by user", jobId);
            }
            catch (OperationCanceledException) when (appStopping.IsCancellationRequested)
            {
                _logger.LogInformation("Email job {JobId} interrupted by app shutdown — will resume on restart", jobId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fatal error processing email job {JobId}", jobId);
                await TrySetJobFailedAsync(jobId, ex.Message);
            }
            finally
            {
                _activeJobs.TryRemove(jobId, out _);
            }
        }

        private async Task ProcessJobSendAsync(
            EmailJob job,
            string htmlBody,
            IEmailJobRepository repo,
            CancellationToken ct
        )
        {
            htmlBody = EmailMessageBuilder.InlineCss(htmlBody);
            var imageData = EmailMessageBuilder.ExtractInlineImages(htmlBody);
            var categoryDisplayName = EmailSubscriptionCategories.DisplayNames.TryGetValue(
                job.Category ?? "",
                out var name
            )
                ? name
                : job.Category;

            // Promote Pending recipients that already have a delivery event to Sent,
            // so the filter below correctly excludes them and they are not re-sent.
            if (NormalizePendingDelivered(job))
                await TryPersistJobAsync(repo, job);

            var pendingRecipients = (job.Recipients ?? new())
                .Where(r =>
                    (r.Status == EmailJobRecipientStatus.Pending || r.Status == EmailJobRecipientStatus.Failed)
                    && r.AttemptCount < job.MaxRecipientAttempts
                    && r.DeliveryStatus == DeliveryStatus.Unknown
                )
                .ToList();

            var stoppedEarly = false;

            if (pendingRecipients.Count > 0)
            {
#if DEBUG
                // In DEBUG, send a single representative message via the transport router
                if (pendingRecipients.Count > 0)
                {
                    var debugRecipient = pendingRecipients[0];
                    var debugToken =
                        (debugRecipient.HomeId != Guid.Empty && !string.IsNullOrEmpty(_appBaseUrl))
                            ? _tokenService.GenerateToken(debugRecipient.HomeId, debugRecipient.Email)
                            : null;
                    var debugFooter = EmailMessageBuilder.BuildUnsubscribeFooter(
                        _appBaseUrl,
                        categoryDisplayName,
                        debugToken
                    );
                    var debugMessage = new MimeMessage();
                    debugMessage.From.Add(new MailboxAddress(job.FromDisplay, job.FromEmail));
                    debugMessage.Subject = $"[DEBUG {job.TotalRecipients} recipients] {job.Subject}";
                    debugMessage.ReplyTo.Add(new MailboxAddress(job.FromDisplay, job.FromEmail));
                    debugMessage.Bcc.Add(new MailboxAddress(null, "bill@cohad.org"));
                    debugMessage.Bcc.Add(new MailboxAddress(null, "bilongtest@gmail.com"));
                    debugMessage.To.Add(new GroupAddress("Private Recipients"));
                    debugMessage.Body = EmailMessageBuilder.BuildBodyWithImages(
                        imageData.ProcessedHtml + debugFooter,
                        imageData.Images
                    );
                    if (debugToken != null && !string.IsNullOrEmpty(_appBaseUrl))
                    {
                        var unsubUrl =
                            $"{_appBaseUrl}/api/email/unsubscribe/{job.Category}?token={Uri.EscapeDataString(debugToken)}";
                        debugMessage.Headers.Add("List-Unsubscribe", $"<{unsubUrl}>");
                        debugMessage.Headers.Add("List-Unsubscribe-Post", "List-Unsubscribe=One-Click");
                    }

                    // In DEBUG, always use SMTP transport (debug sends go to hard-coded Bcc
                    // addresses, not the actual recipient, so per-recipient routing would be misleading)
                    var transport = _transportRouter.GetTransportForRecipient(string.Empty);
                    var result = await transport.SendAsync(debugMessage, job.Id.ToString(), debugRecipient.Email, ct);

                    // Mark all recipients as sent in DEBUG mode
                    foreach (var r in pendingRecipients)
                    {
                        r.Status = result.Success ? EmailJobRecipientStatus.Sent : EmailJobRecipientStatus.Failed;
                        r.SentUtc = result.Success ? DateTime.UtcNow : null;
                        r.Error = result.Error;
                        r.Provider = result.ProviderName;
                        r.ProviderMessageId = result.ProviderMessageId;
                    }
                    job.LastProgressUtc = DateTime.UtcNow;
                    RecalculateCounts(job);
                    if (!await TryPersistJobAsync(repo, job))
                        stoppedEarly = true;
                }
#else
                foreach (var recipient in pendingRecipients)
                {
                    ct.ThrowIfCancellationRequested();

                    var attemptStartedUtc = DateTime.UtcNow;
                    recipient.AttemptCount++;
                    recipient.LastAttemptUtc = attemptStartedUtc;
                    job.LastProgressUtc = attemptStartedUtc;
                    if (!await TryPersistJobAsync(repo, job))
                    {
                        stoppedEarly = true;
                        break;
                    }

                    try
                    {
                        var token =
                            (recipient.HomeId != Guid.Empty && !string.IsNullOrEmpty(_appBaseUrl))
                                ? _tokenService.GenerateToken(recipient.HomeId, recipient.Email)
                                : null;

                        var footer = EmailMessageBuilder.BuildUnsubscribeFooter(
                            _appBaseUrl,
                            categoryDisplayName,
                            token
                        );
                        var htmlWithFooter = imageData.ProcessedHtml + footer;

                        var message = new MimeMessage();
                        message.From.Add(new MailboxAddress(job.FromDisplay, job.FromEmail));
                        message.Subject = job.Subject;
                        message.ReplyTo.Add(new MailboxAddress(job.FromDisplay, job.FromEmail));
                        message.To.Add(new MailboxAddress("", recipient.Email));
                        message.Body = EmailMessageBuilder.BuildBodyWithImages(htmlWithFooter, imageData.Images);

                        if (token != null && !string.IsNullOrEmpty(_appBaseUrl))
                        {
                            var unsubUrl =
                                $"{_appBaseUrl}/api/email/unsubscribe/{job.Category}?token={Uri.EscapeDataString(token)}";
                            message.Headers.Add("List-Unsubscribe", $"<{unsubUrl}>");
                            message.Headers.Add("List-Unsubscribe-Post", "List-Unsubscribe=One-Click");
                        }

                        var transport = _transportRouter.GetTransportForRecipient(recipient.Email);
                        var result = await transport.SendAsync(message, job.Id.ToString(), recipient.Email, ct);

                        recipient.Provider = result.ProviderName;
                        if (!string.IsNullOrEmpty(result.ProviderMessageId))
                            recipient.ProviderMessageId = result.ProviderMessageId;

                        if (result.Success)
                        {
                            recipient.Status = EmailJobRecipientStatus.Sent;
                            recipient.SentUtc = DateTime.UtcNow;
                            recipient.Error = null;
                        }
                        else
                        {
                            _logger.LogWarning(
                                "Failed to send email to {Email} in job {JobId} via {Provider}: {Error}",
                                recipient.Email,
                                job.Id,
                                result.ProviderName,
                                result.Error
                            );
                            recipient.Status = EmailJobRecipientStatus.Failed;
                            recipient.Error = result.Error;
                        }
                        job.LastProgressUtc = DateTime.UtcNow;
                    }
                    catch (OperationCanceledException)
                    {
                        throw; // Let cancellation propagate
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Unexpected error sending email to {Email} in job {JobId}",
                            recipient.Email,
                            job.Id
                        );
                        recipient.Status = EmailJobRecipientStatus.Failed;
                        recipient.Error = ex.Message;
                        job.LastProgressUtc = DateTime.UtcNow;
                    }

                    RecalculateCounts(job);
                    // Check cancellation before persisting to avoid overwriting a Cancelled status
                    ct.ThrowIfCancellationRequested();
                    // Persist progress after every recipient for maximum recovery granularity
                    if (!await TryPersistJobAsync(repo, job))
                    {
                        stoppedEarly = true;
                        break;
                    }

                    await NotifyProgressAsync(job);
                }
#endif
            }

            if (stoppedEarly)
            {
                // Re-read the job to check if it was cancelled or deleted (not just contention).
                var latest = await repo.GetByIdAsync(job.Id);
                if (latest == null || latest.Status == EmailJobStatus.Cancelled)
                {
                    if (latest != null)
                        await NotifyCompletedAsync(latest);
                    return;
                }

                _logger.LogWarning(
                    "Email job {JobId} stopped early due to persistent concurrency conflicts (sent {Sent}/{Total}) — re-queuing for remaining recipients",
                    job.Id,
                    job.SentCount,
                    job.TotalRecipients
                );
                await _queue.EnqueueAsync(job.Id, ct);
                await NotifyProgressAsync(latest);
                return;
            }

            MarkCappedRecipientsAsFailed(job);

            // Derive final counts from recipient statuses (authoritative source of truth)
            RecalculateCounts(job);

            if (job.FailedCount == 0)
                job.Status = EmailJobStatus.Completed;
            else if (job.SentCount == 0)
                job.Status = EmailJobStatus.Failed;
            else
                job.Status = EmailJobStatus.PartiallyCompleted;

            job.CompletedUtc = DateTime.UtcNow;
            if (!await TryPersistJobAsync(repo, job))
            {
                var latest = await repo.GetByIdAsync(job.Id);
                if (latest != null)
                    await NotifyCompletedAsync(latest);
                return;
            }

            await NotifyCompletedAsync(job);

            _logger.LogInformation(
                "Email job {JobId} finished: status={Status}, sent={Sent}, failed={Failed}",
                job.Id,
                job.Status,
                job.SentCount,
                job.FailedCount
            );
        }

        /// <summary>
        /// In MockData mode, simulate sends without actual SMTP.
        /// </summary>
        private async Task ProcessJobMockAsync(EmailJob job, IEmailJobRepository repo, CancellationToken ct)
        {
            if (!string.IsNullOrWhiteSpace(_mockJobFatalError))
            {
                ct.ThrowIfCancellationRequested();

                // Avoid overwriting a user-cancelled job (CancelJob persists Cancelled via the repository).
                // Cancelled is the authoritative terminal state.
                var latest = await repo.GetByIdAsync(job.Id);
                if (latest != null && latest.Status == EmailJobStatus.Cancelled)
                {
                    await NotifyCompletedAsync(latest);
                    _logger.LogInformation("Email job {JobId} skipped JobFatalError because it was cancelled", job.Id);
                    return;
                }

                job.Status = EmailJobStatus.Failed;
                job.LastError = _mockJobFatalError.Trim();
                job.CompletedUtc = DateTime.UtcNow;
                if (await TryPersistJobAsync(repo, job))
                {
                    await NotifyCompletedAsync(job);
                }
                else
                {
                    latest = await repo.GetByIdAsync(job.Id);
                    if (latest != null)
                        await NotifyCompletedAsync(latest);
                }
                _logger.LogWarning("Email job {JobId} failed in mock mode (JobFatalError)", job.Id);
                return;
            }

            // Promote Pending recipients that already have a delivery event to Sent.
            if (NormalizePendingDelivered(job))
                await TryPersistJobAsync(repo, job);

            var pendingRecipients = (job.Recipients ?? new())
                .Where(r =>
                    (r.Status == EmailJobRecipientStatus.Pending || r.Status == EmailJobRecipientStatus.Failed)
                    && r.AttemptCount < job.MaxRecipientAttempts
                    && r.DeliveryStatus == DeliveryStatus.Unknown
                )
                .ToList();

            var stoppedEarly = false;
            foreach (var recipient in pendingRecipients)
            {
                ct.ThrowIfCancellationRequested();
                var attemptStartedUtc = DateTime.UtcNow;
                recipient.AttemptCount++;
                recipient.LastAttemptUtc = attemptStartedUtc;
                job.LastProgressUtc = attemptStartedUtc;
                if (!await TryPersistJobAsync(repo, job))
                {
                    stoppedEarly = true;
                    break;
                }

                if (_mockDelayMilliseconds > 0)
                    await Task.Delay(_mockDelayMilliseconds, ct);

                var mockFailed =
                    _mockFailAllRecipients
                    || (_mockRandomFailureProbability > 0 && _mockRandom.NextDouble() < _mockRandomFailureProbability);

                if (mockFailed)
                {
                    recipient.Status = EmailJobRecipientStatus.Failed;
                    recipient.Error = "Mock send failure (simulated).";
                    recipient.SentUtc = null;
                }
                else
                {
                    recipient.Status = EmailJobRecipientStatus.Sent;
                    recipient.SentUtc = DateTime.UtcNow;
                    recipient.Error = null;
                }

                job.LastProgressUtc = DateTime.UtcNow;

                RecalculateCounts(job);
                ct.ThrowIfCancellationRequested();
                if (!await TryPersistJobAsync(repo, job))
                {
                    stoppedEarly = true;
                    break;
                }

                await NotifyProgressAsync(job);
            }

            if (stoppedEarly)
            {
                var latest = await repo.GetByIdAsync(job.Id);
                if (latest == null || latest.Status == EmailJobStatus.Cancelled)
                {
                    if (latest != null)
                        await NotifyCompletedAsync(latest);
                    return;
                }

                _logger.LogWarning(
                    "Mock email job {JobId} stopped early due to persistent concurrency conflicts — re-queuing",
                    job.Id
                );
                await _queue.EnqueueAsync(job.Id, ct);
                await NotifyProgressAsync(latest);
                return;
            }

            MarkCappedRecipientsAsFailed(job);

            RecalculateCounts(job);
            if (job.FailedCount == 0)
                job.Status = EmailJobStatus.Completed;
            else if (job.SentCount == 0)
                job.Status = EmailJobStatus.Failed;
            else
                job.Status = EmailJobStatus.PartiallyCompleted;
            job.CompletedUtc = DateTime.UtcNow;
            if (!await TryPersistJobAsync(repo, job))
            {
                var latest = await repo.GetByIdAsync(job.Id);
                if (latest != null)
                    await NotifyCompletedAsync(latest);
                return;
            }

            await NotifyCompletedAsync(job);

            _logger.LogInformation(
                "Email job {JobId} completed in mock mode: status={Status}, sent={Sent}, failed={Failed}",
                job.Id,
                job.Status,
                job.SentCount,
                job.FailedCount
            );
        }

        /// <summary>
        /// Requests cancellation of a running job. Called from the cancel API endpoint.
        /// </summary>
        public bool RequestCancellation(Guid jobId)
        {
            if (_activeJobs.TryGetValue(jobId, out var cts))
            {
                cts.Cancel();
                return true;
            }

            return false;
        }

        /// <summary>
        /// Returns true if the processor is currently working on the given job.
        /// </summary>
        public bool IsJobActive(Guid jobId) => _activeJobs.ContainsKey(jobId);

        private async Task NotifyProgressAsync(EmailJob job)
        {
            try
            {
                await _hubContext
                    .Clients.Group(EmailJobHub.EmailSendersGroupName)
                    .SendAsync(
                        "EmailJobProgress",
                        new
                        {
                            jobId = job.Id,
                            status = job.Status.ToString(),
                            sentCount = job.SentCount,
                            failedCount = job.FailedCount,
                            totalRecipients = job.TotalRecipients,
                        }
                    );
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to send SignalR progress notification for job {JobId}", job.Id);
            }
        }

        private async Task NotifyCompletedAsync(EmailJob job)
        {
            try
            {
                await _hubContext
                    .Clients.Group(EmailJobHub.EmailSendersGroupName)
                    .SendAsync(
                        "EmailJobCompleted",
                        new
                        {
                            jobId = job.Id,
                            status = job.Status.ToString(),
                            sentCount = job.SentCount,
                            failedCount = job.FailedCount,
                            totalRecipients = job.TotalRecipients,
                            lastError = job.LastError,
                        }
                    );
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to send SignalR completion notification for job {JobId}", job.Id);
            }
        }

        private async Task TrySetJobFailedAsync(Guid jobId, string error)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<IEmailJobRepository>();
                var job = await repo.GetByIdAsync(jobId);
                if (job != null && job.Status != EmailJobStatus.Cancelled)
                {
                    try
                    {
                        job.Status = EmailJobStatus.Failed;
                        job.LastError = error;
                        job.CompletedUtc = DateTime.UtcNow;
                        await repo.UpdateAsync(job);
                        await NotifyCompletedAsync(job);
                    }
                    catch (EmailJobConcurrencyException)
                    {
                        var latest = await repo.GetByIdAsync(jobId);
                        if (latest != null)
                            await NotifyCompletedAsync(latest);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to mark email job {JobId} as failed", jobId);
            }
        }
    }
}
