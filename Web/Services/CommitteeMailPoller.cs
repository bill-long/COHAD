#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
        private readonly ILogger<CommitteeMailPoller> _logger;
        private readonly TimeSpan _pollInterval;
        private readonly bool _enabled;

        private const string ProcessedFolderName = "COHAD Processed";
        private const string ForwardCategory = "committee-forward";

        public CommitteeMailPoller(
            IServiceScopeFactory scopeFactory,
            EmailJobQueue emailJobQueue,
            IGraphMailReader graphMailReader,
            IConfiguration config,
            ILogger<CommitteeMailPoller> logger
        )
        {
            _scopeFactory = scopeFactory;
            _emailJobQueue = emailJobQueue;
            _graphMailReader = graphMailReader;
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
                if (string.IsNullOrEmpty(graphId) || string.IsNullOrEmpty(internetMessageId))
                    continue;

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

            var job = new EmailJob
            {
                Id = Guid.NewGuid(),
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
                InternetMessageId = internetMessageId,
                ReplyToEmail = senderEmail,
                ReplyToDisplay = senderName,
                Recipients = recipients,
            };

            // Store HTML body in blob storage
            job.ContentBlobPath = $"email-jobs/{job.Id:D}.html";
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(htmlBody)))
            {
                await fileStore.UploadAsync(job.ContentBlobPath, stream, "text/html");
            }

            await emailJobRepo.AddAsync(job);

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
                Id = Guid.NewGuid(),
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

            await heldMessageRepo.AddAsync(held);

            _logger.LogInformation(
                "Held message {InternetMessageId} in {Mailbox} — sender {Sender} not in directory",
                internetMessageId,
                committee.CommitteeEmail,
                senderEmail
            );

            // Move to processed folder using Graph API id (the held record in Cosmos tracks via InternetMessageId)
            await MoveToProcessedSafe(committee.CommitteeEmail, graphId, processedFolderId, ct);

            // Notify administrators
            await NotifyAdminsOfHeldMessageAsync(committee, held, userRepo, ct);
        }

        private async Task NotifyAdminsOfHeldMessageAsync(
            Committee committee,
            HeldMessage held,
            IUserRepository userRepo,
            CancellationToken ct
        )
        {
            try
            {
                var users = await userRepo.GetAllAsync();
                var admins = users
                    .Where(u => u.Roles?.Contains(User.Role.Administrator) == true)
                    .Where(u => !string.IsNullOrWhiteSpace(u.Emails))
                    .ToList();

                if (admins.Count == 0)
                {
                    _logger.LogWarning("No administrators with email addresses found for held message notification");
                    return;
                }

                var recipients = admins
                    .Select(a => new EmailJobRecipient
                    {
                        Email = a.Emails!.Split(new[] { ',', '|', ';' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(e => e.Trim())
                            .FirstOrDefault(e => !string.IsNullOrWhiteSpace(e)) ?? "",
                        Status = EmailJobRecipientStatus.Pending,
                    })
                    .Where(r => !string.IsNullOrWhiteSpace(r.Email))
                    .ToList();

                if (recipients.Count == 0)
                    return;

                var subject = $"[COHAD] Message held for review — {committee.DisplayName}";
                var html = BuildHeldNotificationHtml(committee, held);

                var job = new EmailJob
                {
                    Id = Guid.NewGuid(),
                    Status = EmailJobStatus.Queued,
                    Category = "admin-notification",
                    FromEmail = committee.CommitteeEmail,
                    FromDisplay = "COHAD Mail Gateway",
                    Subject = subject,
                    CreatedUtc = DateTime.UtcNow,
                    CreatedByUserId = "system:mail-poller",
                    CreatedByDisplayName = "Committee Mail Poller",
                    MaxRecipientAttempts = 2,
                    TotalRecipients = recipients.Count,
                    GroupRecipients = false,
                    Recipients = recipients,
                };

                job.ContentBlobPath = $"email-jobs/{job.Id:D}.html";
                using var scope = _scopeFactory.CreateScope();
                var fileStore = scope.ServiceProvider.GetRequiredService<IDocumentFileStore>();
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(html)))
                {
                    await fileStore.UploadAsync(job.ContentBlobPath, stream, "text/html");
                }

                var emailJobRepo = scope.ServiceProvider.GetRequiredService<IEmailJobRepository>();
                await emailJobRepo.AddAsync(job);
                await _emailJobQueue.EnqueueAsync(job.Id, ct);

                _logger.LogInformation(
                    "Sent held-message notification to {Count} administrators for {Committee}",
                    recipients.Count,
                    committee.DisplayName
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send held-message notification for {Committee}", committee.DisplayName);
            }
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

        private static string BuildHeldNotificationHtml(Committee committee, HeldMessage held)
        {
            return $"""
                <p>A message was received in the <strong>{System.Net.WebUtility.HtmlEncode(committee.DisplayName)}</strong>
                mailbox ({System.Net.WebUtility.HtmlEncode(committee.CommitteeEmail)}) from a sender
                not listed in the COHAD directory. It has been held for your review.</p>

                <table style="border-collapse:collapse;margin:16px 0">
                    <tr><td style="padding:4px 12px 4px 0;font-weight:bold">From:</td>
                        <td style="padding:4px 0">{System.Net.WebUtility.HtmlEncode(held.SenderName ?? "")} &lt;{System.Net.WebUtility.HtmlEncode(held.SenderEmail)}&gt;</td></tr>
                    <tr><td style="padding:4px 12px 4px 0;font-weight:bold">Subject:</td>
                        <td style="padding:4px 0">{System.Net.WebUtility.HtmlEncode(held.Subject ?? "(no subject)")}</td></tr>
                    <tr><td style="padding:4px 12px 4px 0;font-weight:bold">Received:</td>
                        <td style="padding:4px 0">{held.ReceivedUtc:f} UTC</td></tr>
                </table>

                <p>Please log in to COHAD and navigate to <strong>Manage Committees → {System.Net.WebUtility.HtmlEncode(committee.DisplayName)}</strong>
                to approve or reject this message.</p>
                """;
        }
    }
}
