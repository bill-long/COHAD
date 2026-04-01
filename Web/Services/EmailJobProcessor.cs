using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Smtp;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MimeKit;
using Web.Configuration;
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
        private readonly IHubContext<EmailJobHub> _hubContext;
        private readonly ILogger<EmailJobProcessor> _logger;
        private readonly SmtpOptions _smtpOptions;
        private readonly string _appBaseUrl;
        private readonly bool _isMockMode;

        // Tracks in-memory cancellation tokens so the cancel API can stop a running job.
        private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeJobs = new();

        private const int MaxSmtpReconnectAttempts = 3;

        public EmailJobProcessor(
            EmailJobQueue queue,
            IServiceScopeFactory scopeFactory,
            IUnsubscribeTokenService tokenService,
            IHubContext<EmailJobHub> hubContext,
            IConfiguration config,
            IWebHostEnvironment env,
            ILogger<EmailJobProcessor> logger)
        {
            _queue = queue;
            _scopeFactory = scopeFactory;
            _tokenService = tokenService;
            _hubContext = hubContext;
            _logger = logger;
            _smtpOptions = new SmtpOptions
            {
                SmtpHost = config["SmtpHost"],
                SmtpUser = config["SmtpUser"],
                SmtpPassword = config["SmtpPassword"]
            };
            _appBaseUrl = (config["AppBaseUrl"] ?? "").TrimEnd('/');
            _isMockMode = env.IsEnvironment("MockData");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
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
                    if (ct.IsCancellationRequested) break;
                    _logger.LogInformation("Resuming incomplete email job {JobId} (status={Status}, sent={Sent}/{Total})",
                        job.Id, job.Status, job.SentCount, job.TotalRecipients);
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

                // Mark as in-progress
                job.Status = EmailJobStatus.InProgress;
                job.StartedUtc ??= DateTime.UtcNow;
                await repo.UpdateAsync(job);

                // Load HTML content from blob storage
                string htmlBody;
                var blobResult = await fileStore.DownloadAsync(job.ContentBlobPath);
                if (blobResult == null)
                {
                    job.Status = EmailJobStatus.Failed;
                    job.LastError = "Email content not found in storage.";
                    job.CompletedUtc = DateTime.UtcNow;
                    await repo.UpdateAsync(job);
                    await NotifyCompletedAsync(job);
                    _logger.LogError("Email job {JobId} failed: content blob not found at {Path}", jobId, job.ContentBlobPath);
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
                    await ProcessJobSmtpAsync(job, htmlBody, repo, jobCts.Token);
                }
            }
            catch (OperationCanceledException) when (jobCts.IsCancellationRequested && !appStopping.IsCancellationRequested)
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

        private async Task ProcessJobSmtpAsync(EmailJob job, string htmlBody, IEmailJobRepository repo, CancellationToken ct)
        {
            var imageData = EmailMessageBuilder.ExtractInlineImages(htmlBody);
            var categoryDisplayName = EmailSubscriptionCategories.DisplayNames
                .TryGetValue(job.Category ?? "", out var name) ? name : job.Category;

            var pendingRecipients = (job.Recipients ?? new())
                .Where(r => r.Status == EmailJobRecipientStatus.Pending || r.Status == EmailJobRecipientStatus.Failed)
                .ToList();

            using var protocolLog = new MemoryStream();
            var logger = new ProtocolLogger(protocolLog);
            SmtpClient smtpClient = null;

            try
            {
                smtpClient = await ConnectSmtpAsync(logger, ct);

#if DEBUG
                // In DEBUG, send a single representative message instead of the full batch
                if (pendingRecipients.Count > 0)
                {
                    var debugRecipient = pendingRecipients[0];
                    var debugToken = (debugRecipient.HomeId != Guid.Empty && !string.IsNullOrEmpty(_appBaseUrl))
                        ? _tokenService.GenerateToken(debugRecipient.HomeId, debugRecipient.Email)
                        : null;
                    var debugFooter = EmailMessageBuilder.BuildUnsubscribeFooter(_appBaseUrl, categoryDisplayName, debugToken);
                    var debugMessage = new MimeMessage();
                    debugMessage.From.Add(new MailboxAddress(job.FromDisplay, job.FromEmail));
                    debugMessage.Subject = $"[DEBUG {job.TotalRecipients} recipients] {job.Subject}";
                    debugMessage.ReplyTo.Add(new MailboxAddress(job.FromDisplay, job.FromEmail));
                    debugMessage.Bcc.Add(new MailboxAddress(null, "bill@cohad.org"));
                    debugMessage.Bcc.Add(new MailboxAddress(null, "bilongtest@gmail.com"));
                    debugMessage.To.Add(new GroupAddress("Private Recipients"));
                    debugMessage.Body = EmailMessageBuilder.BuildBodyWithImages(imageData.ProcessedHtml + debugFooter, imageData.Images);
                    if (debugToken != null && !string.IsNullOrEmpty(_appBaseUrl))
                    {
                        var unsubUrl = $"{_appBaseUrl}/api/email/unsubscribe/{job.Category}?token={Uri.EscapeDataString(debugToken)}";
                        debugMessage.Headers.Add("List-Unsubscribe", $"<{unsubUrl}>");
                        debugMessage.Headers.Add("List-Unsubscribe-Post", "List-Unsubscribe=One-Click");
                    }
                    await smtpClient.SendAsync(debugMessage, ct);

                    // Mark all recipients as sent in DEBUG mode
                    foreach (var r in pendingRecipients)
                    {
                        r.Status = EmailJobRecipientStatus.Sent;
                        r.SentUtc = DateTime.UtcNow;
                        job.SentCount++;
                    }
                    await repo.UpdateAsync(job);
                }
#else
                foreach (var recipient in pendingRecipients)
                {
                    ct.ThrowIfCancellationRequested();

                    // Reset protocol log between messages to bound memory
                    protocolLog.SetLength(0);

                    try
                    {
                        // Ensure SMTP is connected (reconnect if needed)
                        if (!smtpClient.IsConnected)
                        {
                            smtpClient.Dispose();
                            protocolLog.SetLength(0);
                            logger = new ProtocolLogger(protocolLog);
                            smtpClient = await ConnectSmtpAsync(logger, ct);
                        }

                        var token = (recipient.HomeId != Guid.Empty && !string.IsNullOrEmpty(_appBaseUrl))
                            ? _tokenService.GenerateToken(recipient.HomeId, recipient.Email)
                            : null;

                        var footer = EmailMessageBuilder.BuildUnsubscribeFooter(_appBaseUrl, categoryDisplayName, token);
                        var htmlWithFooter = imageData.ProcessedHtml + footer;

                        var message = new MimeMessage();
                        message.From.Add(new MailboxAddress(job.FromDisplay, job.FromEmail));
                        message.Subject = job.Subject;
                        message.ReplyTo.Add(new MailboxAddress(job.FromDisplay, job.FromEmail));
                        message.To.Add(new MailboxAddress("", recipient.Email));
                        message.Body = EmailMessageBuilder.BuildBodyWithImages(htmlWithFooter, imageData.Images);

                        if (token != null && !string.IsNullOrEmpty(_appBaseUrl))
                        {
                            var unsubUrl = $"{_appBaseUrl}/api/email/unsubscribe/{job.Category}?token={Uri.EscapeDataString(token)}";
                            message.Headers.Add("List-Unsubscribe", $"<{unsubUrl}>");
                            message.Headers.Add("List-Unsubscribe-Post", "List-Unsubscribe=One-Click");
                        }

                        await smtpClient.SendAsync(message, ct);

                        recipient.Status = EmailJobRecipientStatus.Sent;
                        recipient.SentUtc = DateTime.UtcNow;
                        job.SentCount++;
                    }
                    catch (OperationCanceledException)
                    {
                        throw; // Let cancellation propagate
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to send email to {Email} in job {JobId}", recipient.Email, job.Id);
                        recipient.Status = EmailJobRecipientStatus.Failed;
                        recipient.Error = ex.Message;
                        job.FailedCount++;
                    }

                    // Persist progress after every recipient for maximum recovery granularity
                    await repo.UpdateAsync(job);
                    await NotifyProgressAsync(job);
                }
#endif
            }
            finally
            {
                if (smtpClient != null)
                {
                    try
                    {
                        if (smtpClient.IsConnected)
                            await smtpClient.DisconnectAsync(true, CancellationToken.None);
                    }
                    catch { /* best-effort disconnect */ }
                    smtpClient.Dispose();
                }
            }

            // Determine final status
            if (job.FailedCount == 0)
                job.Status = EmailJobStatus.Completed;
            else if (job.SentCount == 0)
                job.Status = EmailJobStatus.Failed;
            else
                job.Status = EmailJobStatus.PartiallyCompleted;

            job.CompletedUtc = DateTime.UtcNow;
            await repo.UpdateAsync(job);
            await NotifyCompletedAsync(job);

            _logger.LogInformation("Email job {JobId} finished: status={Status}, sent={Sent}, failed={Failed}",
                job.Id, job.Status, job.SentCount, job.FailedCount);
        }

        /// <summary>
        /// In MockData mode, simulate sends without actual SMTP.
        /// </summary>
        private async Task ProcessJobMockAsync(EmailJob job, IEmailJobRepository repo, CancellationToken ct)
        {
            var pendingRecipients = (job.Recipients ?? new())
                .Where(r => r.Status == EmailJobRecipientStatus.Pending || r.Status == EmailJobRecipientStatus.Failed)
                .ToList();

            foreach (var recipient in pendingRecipients)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(50, ct); // Simulate send latency

                recipient.Status = EmailJobRecipientStatus.Sent;
                recipient.SentUtc = DateTime.UtcNow;
                job.SentCount++;

                await repo.UpdateAsync(job);
                await NotifyProgressAsync(job);
            }

            job.Status = EmailJobStatus.Completed;
            job.CompletedUtc = DateTime.UtcNow;
            await repo.UpdateAsync(job);
            await NotifyCompletedAsync(job);

            _logger.LogInformation("Email job {JobId} completed in mock mode ({Count} recipients)", job.Id, job.SentCount);
        }

        private async Task<SmtpClient> ConnectSmtpAsync(ProtocolLogger logger, CancellationToken ct)
        {
            var client = new SmtpClient(logger);
            await client.ConnectAsync(_smtpOptions.SmtpHost, 587,
                MailKit.Security.SecureSocketOptions.StartTls, ct);
            await client.AuthenticateAsync(_smtpOptions.SmtpUser, _smtpOptions.SmtpPassword, ct);
            return client;
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
                await _hubContext.Clients.Group(EmailJobHub.EmailSendersGroupName)
                    .SendAsync("EmailJobProgress", new
                    {
                        jobId = job.Id,
                        status = job.Status.ToString(),
                        sentCount = job.SentCount,
                        failedCount = job.FailedCount,
                        totalRecipients = job.TotalRecipients
                    });
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
                await _hubContext.Clients.Group(EmailJobHub.EmailSendersGroupName)
                    .SendAsync("EmailJobCompleted", new
                    {
                        jobId = job.Id,
                        status = job.Status.ToString(),
                        sentCount = job.SentCount,
                        failedCount = job.FailedCount,
                        totalRecipients = job.TotalRecipients,
                        lastError = job.LastError
                    });
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
                    job.Status = EmailJobStatus.Failed;
                    job.LastError = error;
                    job.CompletedUtc = DateTime.UtcNow;
                    await repo.UpdateAsync(job);
                    await NotifyCompletedAsync(job);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to mark email job {JobId} as failed", jobId);
            }
        }
    }
}
