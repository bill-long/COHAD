#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.SignalR;
using Web.Hubs;
using Web.Models;
using Web.Services.Repositories;

namespace Web.Services
{
    /// <summary>
    /// Background service that polls committee shared mailboxes for new messages and either
    /// forwards them to committee members (via <see cref="EmailJobProcessor"/>) or holds them
    /// for admin moderation when the sender is not in the directory.
    /// </summary>
    public sealed class CommitteeMailPoller : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly EmailJobQueue _emailJobQueue;
        private readonly IGraphMailReader _graphMailReader;
        private readonly IHubContext<HeldMessageNotificationsHub> _heldMessageHub;
        private readonly ILogger<CommitteeMailPoller> _logger;
        private readonly TimeSpan _pollInterval;
        private readonly bool _enabled;

        private const string ProcessedFolderName = "COHAD Processed";
        private const string ForwardCategory = "committee-forward";

        public CommitteeMailPoller(
            IServiceScopeFactory scopeFactory,
            EmailJobQueue emailJobQueue,
            IGraphMailReader graphMailReader,
            IHubContext<HeldMessageNotificationsHub> heldMessageHub,
            IConfiguration config,
            ILogger<CommitteeMailPoller> logger
        )
        {
            _scopeFactory = scopeFactory;
            _emailJobQueue = emailJobQueue;
            _graphMailReader = graphMailReader;
            _heldMessageHub = heldMessageHub;
            _logger = logger;

            _enabled = config.GetValue("CommitteeForwarding:Enabled", false);
            var intervalMinutes = config.GetValue("CommitteeForwarding:PollIntervalMinutes", 10);
            _pollInterval = TimeSpan.FromMinutes(Math.Max(1, intervalMinutes));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_enabled)
            {
                _logger.LogInformation("Committee mail poller is disabled (CommitteeForwarding:Enabled = false)");
                return;
            }

            _logger.LogInformation(
                "Committee mail poller started. Polling every {Interval} minutes",
                _pollInterval.TotalMinutes
            );

            // Short initial delay so the app finishes starting up
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await PollAllCommitteesAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unhandled error in committee mail poller cycle");
                }

                await Task.Delay(_pollInterval, stoppingToken);
            }
        }

        internal async Task PollAllCommitteesAsync(CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var committeeRepo = scope.ServiceProvider.GetRequiredService<ICommitteeRepository>();
            var emailJobRepo = scope.ServiceProvider.GetRequiredService<IEmailJobRepository>();
            var heldMessageRepo = scope.ServiceProvider.GetRequiredService<IHeldMessageRepository>();
            var residentRepo = scope.ServiceProvider.GetRequiredService<IResidentRepository>();
            var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            var fileStore = scope.ServiceProvider.GetRequiredService<IDocumentFileStore>();

            var committees = await committeeRepo.GetAllAsync();
            var enabled = committees
                .Where(c => c.ForwardingEnabled && !string.IsNullOrWhiteSpace(c.CommitteeEmail))
                .ToList();

            if (enabled.Count == 0)
            {
                _logger.LogDebug("No committees with forwarding enabled");
                return;
            }

            foreach (var committee in enabled)
            {
                string pollStatus;
                string? pollError;
                try
                {
                    await PollCommitteeAsync(
                        committee,
                        committeeRepo,
                        emailJobRepo,
                        heldMessageRepo,
                        residentRepo,
                        userRepo,
                        fileStore,
                        ct
                    );

                    pollStatus = "Success";
                    pollError = null;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Failed to poll committee mailbox {Mailbox}", committee.CommitteeEmail);
                    pollStatus = "Failed";
                    pollError = ex.Message;
                }

                // Reload the committee to avoid clobbering concurrent UI/API edits (last-write-wins).
                // Only update the poll status fields.
                var fresh = await committeeRepo.GetByIdAsync(committee.Id);
                if (fresh != null)
                {
                    fresh.LastPollUtc = DateTime.UtcNow;
                    fresh.LastPollStatus = pollStatus;
                    fresh.LastPollError = pollError;
                    await committeeRepo.UpsertAsync(fresh);
                }
            }
        }

        private async Task PollCommitteeAsync(
            Committee committee,
            ICommitteeRepository committeeRepo,
            IEmailJobRepository emailJobRepo,
            IHeldMessageRepository heldMessageRepo,
            IResidentRepository residentRepo,
            IUserRepository userRepo,
            IDocumentFileStore fileStore,
            CancellationToken ct
        )
        {
            var messages = await _graphMailReader.GetInboxMessagesAsync(committee.CommitteeEmail, ct);
            if (messages.Count == 0)
                return;

            _logger.LogInformation(
                "Found {Count} messages in {Mailbox} inbox",
                messages.Count,
                committee.CommitteeEmail
            );

            var processedFolderId = await _graphMailReader.GetOrCreateFolderAsync(
                committee.CommitteeEmail,
                ProcessedFolderName,
                ct
            );

            // Resolve forwarding recipients once for this committee
            var forwardingMembers = (committee.Members ?? new List<CommitteeMember>())
                .Where(m => m.ReceivesForwardedEmail)
                .ToList();

            var recipientResidents = new Dictionary<Guid, Resident>();
            if (forwardingMembers.Count > 0)
            {
                var residentIds = forwardingMembers.Select(m => m.ResidentId).Distinct().ToList();
                var residents = await residentRepo.GetByIdsAsync(residentIds);
                recipientResidents = residents.ToDictionary(r => r.Id);
            }

            foreach (var message in messages)
            {
                ct.ThrowIfCancellationRequested();

                var graphId = message.Id; // Graph API ID — used for move operations
                var internetMessageId = message.InternetMessageId; // RFC 2822 Message-ID — stable across moves
                if (string.IsNullOrEmpty(graphId))
                    continue;

                if (string.IsNullOrEmpty(internetMessageId))
                {
                    _logger.LogWarning(
                        "Message {GraphId} in {Mailbox} has no InternetMessageId — moving to Processed without forwarding",
                        graphId,
                        committee.CommitteeEmail
                    );
                    await MoveToProcessedSafe(committee.CommitteeEmail, graphId, processedFolderId, ct);
                    continue;
                }

                try
                {
                    await ProcessMessageAsync(
                        committee,
                        message,
                        graphId,
                        internetMessageId,
                        processedFolderId,
                        forwardingMembers,
                        recipientResidents,
                        emailJobRepo,
                        heldMessageRepo,
                        residentRepo,
                        userRepo,
                        fileStore,
                        ct
                    );
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(
                        ex,
                        "Error processing message {InternetMessageId} in {Mailbox}",
                        internetMessageId,
                        committee.CommitteeEmail
                    );
                    // Continue with next message
                }
            }
        }

        private async Task ProcessMessageAsync(
            Committee committee,
            Microsoft.Graph.Models.Message message,
            string graphId,
            string internetMessageId,
            string processedFolderId,
            List<CommitteeMember> forwardingMembers,
            Dictionary<Guid, Resident> recipientResidents,
            IEmailJobRepository emailJobRepo,
            IHeldMessageRepository heldMessageRepo,
            IResidentRepository residentRepo,
            IUserRepository userRepo,
            IDocumentFileStore fileStore,
            CancellationToken ct
        )
        {
            // Step 1: Idempotency check — skip if we already created a job for this committee + message
            var existingJob = await emailJobRepo.GetByInternetMessageIdAsync(internetMessageId, committee.CommitteeEmail);
            if (existingJob != null)
            {
                _logger.LogDebug(
                    "Skipping message {InternetMessageId} — job {JobId} already exists",
                    internetMessageId,
                    existingJob.Id
                );
                await MoveToProcessedSafe(committee.CommitteeEmail, graphId, processedFolderId, ct);
                return;
            }

            // Also check if already held
            var existingHeld = await heldMessageRepo.GetByInternetMessageIdAsync(committee.Id, internetMessageId);
            if (existingHeld != null)
            {
                _logger.LogDebug("Skipping message {InternetMessageId} — already held", internetMessageId);
                await MoveToProcessedSafe(committee.CommitteeEmail, graphId, processedFolderId, ct);
                return;
            }

            var senderEmail = message.From?.EmailAddress?.Address;
            var senderName = message.From?.EmailAddress?.Name;

            // Step 2: Sender filtering
            if (committee.ForwardingSenderFilter == ForwardingSenderFilter.DirectoryOnly)
            {
                var shouldHold = false;

                if (string.IsNullOrWhiteSpace(senderEmail))
                {
                    shouldHold = true;
                }
                else
                {
                    var senderResidents = await residentRepo.GetByEmailAsync(senderEmail);
                    if (senderResidents.Count == 0)
                        shouldHold = true;
                }

                if (shouldHold)
                {
                    if (forwardingMembers.Count == 0)
                    {
                        // No recipients to forward to — skip holding and just move to processed
                        _logger.LogDebug(
                            "Unknown sender {Sender} in {Mailbox} but no forwarding recipients — skipping hold",
                            senderEmail,
                            committee.CommitteeEmail
                        );
                        await MoveToProcessedSafe(committee.CommitteeEmail, graphId, processedFolderId, ct);
                        return;
                    }

                    await HoldMessageAsync(
                        committee,
                        graphId,
                        internetMessageId,
                        senderEmail,
                        senderName,
                        message.Subject,
                        message.ReceivedDateTime?.UtcDateTime ?? DateTime.UtcNow,
                        processedFolderId,
                        heldMessageRepo,
                        userRepo,
                        ct
                    );
                    return;
                }
            }

            // Step 3: Create EmailJob for forwarding
            if (forwardingMembers.Count == 0)
            {
                _logger.LogDebug(
                    "No forwarding recipients for {Mailbox}, moving message to processed",
                    committee.CommitteeEmail
                );
                await MoveToProcessedSafe(committee.CommitteeEmail, graphId, processedFolderId, ct);
                return;
            }

            var recipients = forwardingMembers
                .Select(m => recipientResidents.GetValueOrDefault(m.ResidentId))
                .Where(r => r?.EmailAddresses?.Any(e => !string.IsNullOrWhiteSpace(e?.Address)) == true)
                .Select(r => new EmailJobRecipient
                {
                    Email = r!.EmailAddresses.First(e => !string.IsNullOrWhiteSpace(e?.Address)).Address,
                    HomeId = r.HomeId,
                    Status = EmailJobRecipientStatus.Pending,
                })
                .GroupBy(r => r.Email, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            if (recipients.Count == 0)
            {
                _logger.LogWarning(
                    "Committee {Committee} has forwarding members but none have valid email addresses",
                    committee.Id
                );
                await MoveToProcessedSafe(committee.CommitteeEmail, graphId, processedFolderId, ct);
                return;
            }

            // Build the forwarding subject line
            var fwdSubject = message.Subject?.StartsWith("Fwd:", StringComparison.OrdinalIgnoreCase) == true
                    || message.Subject?.StartsWith("FW:", StringComparison.OrdinalIgnoreCase) == true
                ? message.Subject
                : $"Fwd: {message.Subject}";

            // Build the HTML body with forwarding header
            var htmlBody = BuildForwardedHtml(message, senderEmail, senderName);

            // Deterministic job ID from committee + message ID for write-time idempotency.
            // If two poll cycles race, the second CreateItemAsync will fail with 409 Conflict.
            var jobId = DeterministicGuid(committee.CommitteeEmail, internetMessageId);

            // Download non-inline attachments from Graph API
            var attachments = new List<EmailJobAttachment>();
            if (message.HasAttachments == true)
            {
                try
                {
                    var fullMessage = await _graphMailReader.GetMessageWithAttachmentsAsync(
                        committee.CommitteeEmail, graphId, ct);

                    if (fullMessage?.Attachments != null)
                    {
                        int attachIndex = 0;
                        foreach (var att in fullMessage.Attachments)
                        {
                            if (att is Microsoft.Graph.Models.FileAttachment fileAtt
                                && fileAtt.IsInline != true
                                && fileAtt.ContentBytes != null
                                && fileAtt.ContentBytes.Length > 0)
                            {
                                var safeName = SanitizeFileName(fileAtt.Name ?? $"attachment-{attachIndex}");
                                var blobPath = $"email-jobs/{jobId:D}-attachments/{attachIndex:D4}-{safeName}";

                                using (var stream = new MemoryStream(fileAtt.ContentBytes))
                                {
                                    await fileStore.UploadAsync(blobPath, stream,
                                        fileAtt.ContentType ?? "application/octet-stream");
                                }

                                attachments.Add(new EmailJobAttachment
                                {
                                    FileName = safeName,
                                    BlobPath = blobPath,
                                    ContentType = fileAtt.ContentType ?? "application/octet-stream",
                                    Size = fileAtt.ContentBytes.Length,
                                });
                                attachIndex++;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to download attachments for message {InternetMessageId} in {Mailbox}",
                        internetMessageId, committee.CommitteeEmail);
                }
            }

            var job = new EmailJob
            {
                Id = jobId,
                Status = EmailJobStatus.Queued,
                Category = ForwardCategory,
                FromEmail = committee.CommitteeEmail,
                FromDisplay = committee.DisplayName,
                Subject = fwdSubject,
                CreatedUtc = DateTime.UtcNow,
                CreatedByUserId = "system:mail-poller",
                CreatedByDisplayName = "Committee Mail Poller",
                MaxRecipientAttempts = 3,
                TotalRecipients = recipients.Count,
                GroupRecipients = true,
                InternetMessageId = internetMessageId,
                ReplyToEmail = string.IsNullOrWhiteSpace(senderEmail) ? null : senderEmail,
                ReplyToDisplay = string.IsNullOrWhiteSpace(senderEmail) ? null : senderName,
                Attachments = attachments,
                Recipients = recipients,
            };

            // Store HTML body in blob storage (idempotent — ignore conflict if blob already exists from a prior attempt)
            job.ContentBlobPath = $"email-jobs/{job.Id:D}.html";
            try
            {
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(htmlBody)))
                {
                    await fileStore.UploadAsync(job.ContentBlobPath, stream, "text/html");
                }
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 409)
            {
                _logger.LogDebug("Blob {Path} already exists — proceeding with job creation", job.ContentBlobPath);
            }

            try
            {
                await emailJobRepo.AddAsync(job);
            }
            catch (Microsoft.Azure.Cosmos.CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                // Duplicate — another poll cycle already created a job for this message
                _logger.LogDebug(
                    "Job {JobId} already exists for message {InternetMessageId} — skipping",
                    job.Id,
                    internetMessageId
                );
                await MoveToProcessedSafe(committee.CommitteeEmail, graphId, processedFolderId, ct);
                return;
            }

            // Step 4: Enqueue for EmailJobProcessor
            await _emailJobQueue.EnqueueAsync(job.Id, ct);

            _logger.LogInformation(
                "Created forwarding job {JobId} for message {InternetMessageId} in {Mailbox} → {RecipientCount} recipients",
                job.Id,
                internetMessageId,
                committee.CommitteeEmail,
                recipients.Count
            );

            // Step 5: Move to processed (uses Graph API id, not InternetMessageId)
            await MoveToProcessedSafe(committee.CommitteeEmail, graphId, processedFolderId, ct);
        }

        private async Task HoldMessageAsync(
            Committee committee,
            string graphId,
            string internetMessageId,
            string? senderEmail,
            string? senderName,
            string? subject,
            DateTime receivedUtc,
            string processedFolderId,
            IHeldMessageRepository heldMessageRepo,
            IUserRepository userRepo,
            CancellationToken ct
        )
        {
            var held = new HeldMessage
            {
                // Deterministic ID for write-time idempotency (same pattern as EmailJob)
                Id = DeterministicGuid(committee.CommitteeEmail, internetMessageId + ":held"),
                CommitteeId = committee.Id,
                CommitteeEmail = committee.CommitteeEmail,
                InternetMessageId = internetMessageId,
                SenderEmail = senderEmail,
                SenderName = senderName,
                Subject = subject,
                ReceivedUtc = receivedUtc,
                HeldUtc = DateTime.UtcNow,
                Status = HeldMessageStatus.Held,
            };

            try
            {
                await heldMessageRepo.AddAsync(held);
            }
            catch (Microsoft.Azure.Cosmos.CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                _logger.LogDebug(
                    "Held record already exists for message {InternetMessageId} in {Mailbox} — skipping",
                    internetMessageId,
                    committee.CommitteeEmail
                );
                await MoveToProcessedSafe(committee.CommitteeEmail, graphId, processedFolderId, ct);
                return;
            }

            _logger.LogInformation(
                "Held message {InternetMessageId} in {Mailbox} — sender {Sender} not in directory",
                internetMessageId,
                committee.CommitteeEmail,
                senderEmail
            );

            try
            {
                // Signal-only: carry just the committee id. Clients re-fetch the pending list via
                // the authorized REST endpoint, so message details never flow to a stale connection
                // whose owner's moderation rights were revoked after they connected.
                await _heldMessageHub.Clients
                    .Group(HeldMessageNotificationsHub.CommitteeGroupName(held.CommitteeId))
                    .SendAsync("HeldMessagesChanged", new { committeeId = held.CommitteeId }, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send held-message SignalR notification for {Committee}", committee.DisplayName);
            }

            // Move to processed folder using Graph API id (the held record in Cosmos tracks via InternetMessageId)
            await MoveToProcessedSafe(committee.CommitteeEmail, graphId, processedFolderId, ct);
        }

        /// <summary>
        /// Produces a deterministic GUID from two string inputs (committee email + internet message ID).
        /// Ensures that the same message in the same mailbox always maps to the same EmailJob ID,
        /// making CreateItemAsync fail with 409 if two poll cycles race on the same message.
        /// </summary>
        private static Guid DeterministicGuid(string a, string b)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{a}\n{b}"));
            // Truncate SHA-256 to 16 bytes for a GUID
            var guidBytes = new byte[16];
            Array.Copy(bytes, guidBytes, 16);
            return new Guid(guidBytes);
        }

        private async Task MoveToProcessedSafe(
            string mailbox,
            string messageId,
            string processedFolderId,
            CancellationToken ct
        )
        {
            try
            {
                await _graphMailReader.MoveMessageAsync(mailbox, messageId, processedFolderId, ct);
            }
            catch (Exception ex)
            {
                // Non-fatal — message stays in Inbox and will be skipped next poll via idempotency check
                _logger.LogWarning(ex, "Failed to move message {MessageId} to processed folder", messageId);
            }
        }

        private static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sanitized = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            return string.IsNullOrWhiteSpace(sanitized) ? "attachment" : sanitized;
        }

        private static string BuildForwardedHtml(
            Microsoft.Graph.Models.Message message,
            string? senderEmail,
            string? senderName
        )
        {
            var encodedName = System.Net.WebUtility.HtmlEncode(senderName ?? "");
            var encodedEmail = System.Net.WebUtility.HtmlEncode(senderEmail ?? "(unknown sender)");
            var sender = !string.IsNullOrWhiteSpace(senderName)
                ? $"{encodedName} &lt;{encodedEmail}&gt;"
                : encodedEmail;

            var receivedDate = message.ReceivedDateTime?.ToString("f") ?? "Unknown";

            // Handle plain-text vs HTML body content
            string originalBody;
            if (message.Body?.ContentType == Microsoft.Graph.Models.BodyType.Text)
            {
                originalBody = $"<pre style=\"white-space:pre-wrap\">{System.Net.WebUtility.HtmlEncode(message.Body.Content ?? "")}</pre>";
            }
            else
            {
                originalBody = message.Body?.Content ?? "";
            }

            return $"""
                <div style="border-left:2px solid #ccc;padding-left:12px;margin-bottom:16px;color:#555">
                    <p><strong>---------- Forwarded message ----------</strong></p>
                    <p><strong>From:</strong> {sender}<br/>
                    <strong>Date:</strong> {System.Net.WebUtility.HtmlEncode(receivedDate)}<br/>
                    <strong>Subject:</strong> {System.Net.WebUtility.HtmlEncode(message.Subject ?? "")}</p>
                </div>
                {originalBody}
                """;
        }
    }
}
