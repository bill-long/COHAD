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
        private readonly ISpamClassifier _spamClassifier;
        private readonly ILogger<CommitteeMailPoller> _logger;
        private readonly TimeSpan _pollInterval;
        private readonly TimeSpan _antispamHold;
        private readonly bool _enabled;
        private readonly bool _spamClassificationEnabled;
        private readonly SpamConfidence _spamRejectThreshold;
        private readonly string? _invalidThresholdRaw;

        private const string ProcessedFolderName = "COHAD Processed";
        private const string ForwardCategory = EmailJob.CommitteeForwardCategory;
        private const string SpamClassifierReviewer = "system:spam-classifier";

        public CommitteeMailPoller(
            IServiceScopeFactory scopeFactory,
            EmailJobQueue emailJobQueue,
            IGraphMailReader graphMailReader,
            ISpamClassifier spamClassifier,
            IConfiguration config,
            ILogger<CommitteeMailPoller> logger
        )
        {
            _scopeFactory = scopeFactory;
            _emailJobQueue = emailJobQueue;
            _graphMailReader = graphMailReader;
            _spamClassifier = spamClassifier;
            _logger = logger;

            _enabled = config.GetValue("CommitteeForwarding:Enabled", false);
            var intervalMinutes = config.GetValue("CommitteeForwarding:PollIntervalMinutes", 10);
            _pollInterval = JobInterval.FromMinutes(intervalMinutes);

            // Non-directory (held) messages sit in an antispam quarantine for this long before their
            // moderators are notified, giving automatic antispam a chance to act on them first. 0 disables
            // the delay (a held message becomes eligible on the next poll's quarantine sweep).
            var antispamHoldMinutes = config.GetValue("CommitteeForwarding:AntispamHoldMinutes", 60);
            _antispamHold = TimeSpan.FromMinutes(Math.Max(0, antispamHoldMinutes));

            // LLM spam classification of held messages. When enabled, held mail is classified at hold time
            // and messages the classifier flags as spam with at least this confidence are auto-rejected by
            // the antispam sweep instead of notifying moderators. Single kill-switch for both steps.
            _spamClassificationEnabled = config.GetValue("CommitteeForwarding:SpamClassification:Enabled", false);
            // Default tied to the enum member (not a bare "High" literal) so the code default can't drift
            // from SpamConfidence; appsettings.json carries the same value as the overridable config.
            var thresholdRaw = config.GetValue(
                "CommitteeForwarding:SpamClassification:ConfidenceThreshold",
                nameof(SpamConfidence.High)
            );
            // TryParseDefined rejects out-of-range numerics ("4") and comma/flags forms that would
            // otherwise yield a threshold no real confidence meets, silently disabling auto-rejection.
            // On anything invalid, fall back to High and record the raw value to warn.
            if (EnumParse.TryParseDefined<SpamConfidence>(thresholdRaw, out var t)
                && t != SpamConfidence.Unknown)
            {
                _spamRejectThreshold = t;
            }
            else
            {
                _spamRejectThreshold = SpamConfidence.High;
                _invalidThresholdRaw = thresholdRaw;
            }
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

            if (_spamClassificationEnabled)
            {
                if (_invalidThresholdRaw != null)
                    _logger.LogWarning(
                        "Invalid CommitteeForwarding:SpamClassification:ConfidenceThreshold '{Raw}' - "
                            + "expected Low, Medium, or High; falling back to High",
                        _invalidThresholdRaw
                    );

                if (_spamClassifier.IsAvailable)
                    _logger.LogInformation(
                        "Held-message spam classification enabled (auto-reject at confidence >= {Threshold})",
                        _spamRejectThreshold
                    );
                else
                    _logger.LogWarning(
                        "Held-message spam classification is enabled but no Anthropic API key is configured - "
                            + "held messages will not be classified or auto-filtered"
                    );
            }

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
            var fileStore = scope.ServiceProvider.GetRequiredService<IDocumentFileStore>();
            var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();

            var committees = await committeeRepo.GetAllAsync();

            // Surface any held (non-directory) messages whose antispam quarantine window has elapsed.
            // Uses the full committee list to resolve display names/audiences for the notifications.
            // Isolated in its own try/catch so a sweep failure never aborts committee forwarding below.
            try
            {
                await NotifyHeldMessagesPastAntispamHoldAsync(committees, heldMessageRepo, notificationService, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Antispam-hold notification sweep failed");
            }

            var enabled = committees
                .Where(c => c.ForwardingEnabled && !string.IsNullOrWhiteSpace(c.CommitteeEmail))
                .ToList();

            if (enabled.Count == 0)
            {
                _logger.LogDebug("No committees with forwarding enabled");
                return;
            }

            // One suppression read per poll cycle, shared by every committee's recipient selection.
            // Normalize the stored addresses rather than trusting them (a hand-authored row may not
            // be normalized). Only committee FORWARDING depends on this set; holding new
            // non-directory mail (spam/phishing quarantine) and processed-folder cleanup do not. So a
            // throwing read does NOT abort the cycle - it defers only forwarding: a null set is
            // passed through, and each message that would be forwarded is left in its inbox for a
            // later cycle (fail-closed - never forward with an unknown suppression state) while
            // holding still runs. This keeps the safety-critical quarantine independent of the
            // suppression store.
            var suppressionRepo = scope.ServiceProvider.GetRequiredService<IEmailSuppressionRepository>();
            IReadOnlySet<string>? suppressedAddresses;
            try
            {
                suppressedAddresses = EmailSuppression.ActiveNormalizedAddressSet(await suppressionRepo.GetActiveAsync());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to read the email suppression list; deferring committee forwarding this cycle (held-message quarantine and processed-folder cleanup still run)");
                suppressedAddresses = null;
            }

            foreach (var committee in enabled)
            {
                string pollStatus;
                string? pollError;
                try
                {
                    var forwardingDeferred = await PollCommitteeAsync(
                        committee,
                        committeeRepo,
                        emailJobRepo,
                        heldMessageRepo,
                        residentRepo,
                        fileStore,
                        suppressedAddresses,
                        ct
                    );

                    // "Forwarding deferred" only when a message actually stayed in the inbox because
                    // the suppression list was unreadable - not merely because the read failed this
                    // cycle. A committee with an empty inbox, or one whose mail was only held, polled
                    // cleanly and reads Success even during the outage.
                    pollStatus = forwardingDeferred ? "Forwarding deferred" : "Success";
                    pollError = forwardingDeferred ? "Suppression list unavailable; forwarding will retry next cycle." : null;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Failed to poll committee mailbox {Mailbox}", committee.CommitteeEmail);
                    pollStatus = "Failed";
                    pollError = ex.Message;
                }

                await StampPollStatusAsync(committeeRepo, committee.Id, pollStatus, pollError);
            }
        }

        /// <summary>
        /// Records the outcome of a poll attempt on the committee's status fields. Reloads the
        /// committee first (last-write-wins) so a concurrent UI/API edit is not clobbered - only the
        /// poll status fields are written.
        /// </summary>
        private static async Task StampPollStatusAsync(
            ICommitteeRepository committeeRepo,
            string committeeId,
            string pollStatus,
            string? pollError
        )
        {
            var fresh = await committeeRepo.GetByIdAsync(committeeId);
            if (fresh != null)
            {
                fresh.LastPollUtc = DateTime.UtcNow;
                fresh.LastPollStatus = pollStatus;
                fresh.LastPollError = pollError;
                await committeeRepo.UpsertAsync(fresh);
            }
        }

        /// <summary>
        /// Polls one committee's inbox. Returns true if at least one message was left in the inbox
        /// because forwarding was deferred (the suppression list could not be read this cycle), so
        /// the caller can surface that on the committee's poll status only when it actually happened.
        /// </summary>
        private async Task<bool> PollCommitteeAsync(
            Committee committee,
            ICommitteeRepository committeeRepo,
            IEmailJobRepository emailJobRepo,
            IHeldMessageRepository heldMessageRepo,
            IResidentRepository residentRepo,
            IDocumentFileStore fileStore,
            IReadOnlySet<string>? suppressedAddresses,
            CancellationToken ct
        )
        {
            var messages = await _graphMailReader.GetInboxMessagesAsync(committee.CommitteeEmail, ct);
            if (messages.Count == 0)
                return false;

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

            var forwardingDeferred = false;
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
                    forwardingDeferred |= await ProcessMessageAsync(
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
                        fileStore,
                        suppressedAddresses,
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

            return forwardingDeferred;
        }

        /// <summary>
        /// Processes one inbox message (idempotency skip, hold, or forward). Returns true only when
        /// forwarding was deferred because the suppression list was unavailable this cycle - the
        /// message is left in the inbox for a later cycle. Every other outcome (held, forwarded,
        /// skipped, moved to Processed) returns false.
        /// </summary>
        private async Task<bool> ProcessMessageAsync(
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
            IDocumentFileStore fileStore,
            IReadOnlySet<string>? suppressedAddresses,
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
                return false;
            }

            // Also check if already held
            var existingHeld = await heldMessageRepo.GetByInternetMessageIdAsync(committee.Id, internetMessageId);
            if (existingHeld != null)
            {
                _logger.LogDebug("Skipping message {InternetMessageId} — already held", internetMessageId);
                await MoveToProcessedSafe(committee.CommitteeEmail, graphId, processedFolderId, ct);
                return false;
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
                        return false;
                    }

                    await HoldMessageAsync(
                        committee,
                        graphId,
                        internetMessageId,
                        senderEmail,
                        senderName,
                        message.Subject,
                        message.Body?.Content,
                        message.ReceivedDateTime?.UtcDateTime ?? DateTime.UtcNow,
                        processedFolderId,
                        heldMessageRepo,
                        ct
                    );
                    return false;
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
                return false;
            }

            // Fail closed for forwarding when the suppression list could not be read this cycle:
            // leave the message in the inbox (do NOT move it to Processed) so it is retried on a
            // later cycle rather than forwarded with an unknown suppression state. The hold path
            // above already ran for non-directory senders, so quarantine is unaffected.
            if (suppressedAddresses == null)
            {
                _logger.LogWarning(
                    "Suppression list unavailable; deferring forward of {InternetMessageId} in {Mailbox} to a later cycle",
                    internetMessageId,
                    committee.CommitteeEmail
                );
                return true;
            }

            var recipients = CommitteeForwardJob.SelectForwardRecipients(
                forwardingMembers,
                recipientResidents,
                suppressedAddresses
            );

            if (recipients.Count == 0)
            {
                _logger.LogWarning(
                    "Committee {Committee} has forwarding members but none have deliverable email addresses",
                    committee.Id
                );
                await MoveToProcessedSafe(committee.CommitteeEmail, graphId, processedFolderId, ct);
                return false;
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
                Attachments = attachments,
                Recipients = recipients,
            };

            CommitteeForwardJob.ApplyOriginator(job, committee.DisplayName, senderEmail, senderName);

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
                return false;
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
            return false;
        }

        private async Task HoldMessageAsync(
            Committee committee,
            string graphId,
            string internetMessageId,
            string? senderEmail,
            string? senderName,
            string? subject,
            string? body,
            DateTime receivedUtc,
            string processedFolderId,
            IHeldMessageRepository heldMessageRepo,
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
                // NotifiedUtc stays null: the message enters the antispam quarantine window and its
                // moderators are notified later by NotifyHeldMessagesPastAntispamHoldAsync, not now.
                Status = HeldMessageStatus.Held,
                // Spam verdict fields default to Unknown; they are filled in by ClassifyAndStoreVerdictAsync
                // AFTER this record is durably persisted, so the paid classifier never runs pre-persist.
            };

            try
            {
                await heldMessageRepo.AddAsync(held);
            }
            catch (Microsoft.Azure.Cosmos.CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                _logger.LogDebug(
                    "Held record already exists for message {InternetMessageId} in {Mailbox} - skipping",
                    internetMessageId,
                    committee.CommitteeEmail
                );
                await MoveToProcessedSafe(committee.CommitteeEmail, graphId, processedFolderId, ct);
                return;
            }

            // Classify only AFTER the held record is durably persisted, then store the verdict. The classifier
            // is a paid API call; running it before the write (as this originally did) meant any transient
            // non-409 AddAsync failure left the message in the inbox to be re-held - and re-classified,
            // re-billed - every poll cycle. Post-persist, the pre-hold dedup check skips an already-held
            // message on the next cycle, so the classifier runs at most once per message. Skip when no real
            // classifier is configured (IsAvailable) so a disabled classifier does not incur a redundant
            // second write of the same default verdict the record was already added with.
            if (_spamClassificationEnabled && _spamClassifier.IsAvailable)
            {
                await ClassifyAndStoreVerdictAsync(held, body, heldMessageRepo, ct);
            }

            _logger.LogInformation(
                "Held message {InternetMessageId} in {Mailbox} - sender {Sender} not in directory; "
                    + "quarantined for antispam before notifying",
                internetMessageId,
                committee.CommitteeEmail,
                senderEmail
            );

            // Move to processed folder using Graph API id (the held record in Cosmos tracks via InternetMessageId)
            await MoveToProcessedSafe(committee.CommitteeEmail, graphId, processedFolderId, ct);
        }

        /// <summary>
        /// Classifies an already-persisted held message and stores the verdict on it. Runs only after the
        /// record is durable so the paid classifier is never invoked before persistence (a pre-persist call
        /// is re-billed on every poll cycle if the Cosmos write fails). Reads sender/subject from
        /// <paramref name="held"/> so classification always uses exactly what was persisted. Storing the
        /// verdict is best-effort: any failure (concurrency conflict, transient Cosmos error, a since-deleted
        /// record, or a classifier fault) is swallowed so it cannot disrupt the hold flow - the record is
        /// already held and the caller still moves the message to Processed. An unstored verdict leaves the
        /// message unclassified, which only under-filters and never drops or wrongly rejects mail.
        /// </summary>
        private async Task ClassifyAndStoreVerdictAsync(
            HeldMessage held,
            string? body,
            IHeldMessageRepository heldMessageRepo,
            CancellationToken ct
        )
        {
            SpamClassificationResult spam;
            try
            {
                spam = await _spamClassifier.ClassifyAsync(held.SenderEmail, held.SenderName, held.Subject, body, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The classifier is contractually fail-safe, but guard here too: a misbehaving implementation
                // must not disturb the already-held message. Leave the persisted (Unknown) verdict.
                _logger.LogWarning(
                    ex,
                    "Spam classifier threw while holding message {InternetMessageId} - leaving it unclassified",
                    held.InternetMessageId
                );
                return;
            }

            held.SpamVerdict = spam.Verdict;
            held.SpamConfidence = spam.Confidence;
            held.SpamReason = spam.Reason;

            try
            {
                await heldMessageRepo.UpdateAsync(held);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Best-effort: the record is already held and the caller still moves the message to Processed,
                // so a failed verdict write must not propagate (that would skip the move and log a benign
                // transient as an error). A concurrency conflict, a transient Cosmos error, or a since-deleted
                // record all land here; the computed verdict is simply not persisted. There is no
                // re-classification retry - the antispam sweep acts only on the stored verdict, so the message
                // keeps whatever is persisted (usually the default Unknown) and falls through to the normal
                // moderator notification after the quarantine window. That under-filters, never wrongly
                // rejects. Do not assert the resulting stored state here - a concurrent writer may have set it -
                // only that our update did not land.
                _logger.LogWarning(
                    ex,
                    "Could not store spam verdict for held message {HeldId} - leaving the record as last persisted",
                    held.Id
                );
            }
        }

        /// <summary>
        /// Raises the deferred in-app notification for each held (non-directory) message whose antispam
        /// quarantine window has elapsed, then stamps <see cref="HeldMessage.NotifiedUtc"/> so it is only
        /// notified once. Best-effort per message: a failure on one is logged and the rest continue.
        /// </summary>
        internal async Task NotifyHeldMessagesPastAntispamHoldAsync(
            IReadOnlyList<Committee> committees,
            IHeldMessageRepository heldMessageRepo,
            INotificationService notificationService,
            CancellationToken ct
        )
        {
            var cutoff = DateTime.UtcNow - _antispamHold;
            var pending = await heldMessageRepo.GetAwaitingNotificationAsync(cutoff);
            if (pending.Count == 0)
                return;

            var byCommitteeId = new Dictionary<string, Committee>();
            foreach (var c in committees)
                if (!string.IsNullOrEmpty(c.Id))
                    byCommitteeId[c.Id] = c;

            // The query already returns only Held, un-notified messages ordered oldest-first, each with a
            // usable ETag (set by the mapper), so we act on the returned row directly rather than re-reading.
            foreach (var held in pending)
            {
                ct.ThrowIfCancellationRequested();

                // Auto-reject confident spam instead of notifying moderators. Gated by the same
                // enable-flag AND live-classifier check as hold-time classification, so it acts only
                // while a real classifier is configured. Requiring IsAvailable preserves the
                // fail-safe invariant that a classifier outage can only under-filter, never wrongly
                // reject: if the Anthropic key is removed (outage or intentional teardown) the sweep
                // stops acting on already-stored Spam verdicts rather than rejecting mail with no live
                // classifier backing it. Turning the feature off also immediately stops auto-rejecting.
                // No notification was ever raised for a quarantined message, so there is nothing to
                // resolve here.
                if (_spamClassificationEnabled
                    && _spamClassifier.IsAvailable
                    && held.SpamVerdict == SpamVerdict.Spam
                    && held.SpamConfidence >= _spamRejectThreshold)
                {
                    try
                    {
                        await AutoRejectAsSpamAsync(held, heldMessageRepo);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Failed to auto-reject held message {HeldId} classified as spam", held.Id);
                    }
                    continue;
                }

                try
                {
                    // A held message should always carry a CommitteeId, but guard against a legacy/corrupt
                    // record: an empty id would give an unresolvable "committee:" audience (reaching no one),
                    // and a null id would throw on the Dictionary lookup. Fall back to Administrators — who
                    // can manage every committee's held mail — so the message is still surfaced, not dropped.
                    string audienceKey;
                    string displayName;
                    if (string.IsNullOrEmpty(held.CommitteeId))
                    {
                        audienceKey = NotificationAudience.Administrators;
                        displayName = string.IsNullOrWhiteSpace(held.CommitteeEmail) ? "a committee" : held.CommitteeEmail;
                    }
                    else
                    {
                        audienceKey = NotificationAudience.Committee(held.CommitteeId);
                        displayName = byCommitteeId.TryGetValue(held.CommitteeId, out var committee)
                            ? committee.DisplayName
                            : held.CommitteeEmail;
                    }
                    var sender = !string.IsNullOrWhiteSpace(held.SenderName) ? held.SenderName : held.SenderEmail;

                    // Idempotent on target — a re-raise (e.g. after a failed stamp) maps to the same
                    // notification rather than creating a duplicate.
                    await notificationService.RaiseAsync(
                        NotificationType.HeldMessage,
                        audienceKey,
                        NotificationTargetType.HeldMessage,
                        held.Id.ToString("D"),
                        "Held committee email",
                        $"{displayName}: from {sender} — {held.Subject}",
                        $"/manage/approvals?message={held.Id:D}",
                        ct
                    );

                    // Stamp NotifiedUtc under optimistic concurrency. A success proves no writer touched the
                    // message between the query and now, so any moderator who approves/rejects later will see
                    // (and resolve) the notification we just raised.
                    held.NotifiedUtc = DateTime.UtcNow;
                    try
                    {
                        await heldMessageRepo.UpdateAsync(held);
                    }
                    catch (InvalidOperationException)
                    {
                        // A concurrent write (typically a moderator approve/reject) beat our stamp. That
                        // action's own ResolveAsync may have run before our RaiseAsync above and so found no
                        // notification to clear — leaving ours orphaned. Re-read and, if the message is no
                        // longer Held, resolve the notification we raised so it does not escalate as an alert
                        // for an already-handled message. If it is still Held, a later sweep re-stamps it.
                        var after = await heldMessageRepo.GetByIdAsync(held.Id);
                        if (after == null || after.Status != HeldMessageStatus.Held)
                        {
                            await notificationService.ResolveAsync(
                                NotificationTargetType.HeldMessage,
                                held.Id.ToString("D"),
                                "system:antispam-hold",
                                ct
                            );
                        }
                        else
                        {
                            _logger.LogDebug(
                                "Held message {HeldId} changed concurrently while stamping NotifiedUtc — leaving for a later sweep",
                                held.Id
                            );
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Failed to notify held message {HeldId} past antispam hold", held.Id);
                }
            }
        }

        /// <summary>
        /// Marks a held message as rejected because the spam classifier flagged it with sufficient
        /// confidence, without notifying moderators. The record (and its verdict/confidence/reason) is
        /// retained as an audit trail, and the original email stays in the mailbox's Processed folder, so a
        /// false positive is fully recoverable. Update is optimistic-concurrency guarded: a concurrent
        /// write (e.g. a competing sweep or expiry) is tolerated and left for a later sweep to re-evaluate.
        /// </summary>
        private async Task AutoRejectAsSpamAsync(HeldMessage held, IHeldMessageRepository heldMessageRepo)
        {
            held.Status = HeldMessageStatus.Rejected;
            held.ReviewedByUserId = SpamClassifierReviewer;
            held.ReviewedUtc = DateTime.UtcNow;
            // NotifiedUtc intentionally left null - moderators are never notified for auto-rejected spam.

            try
            {
                await heldMessageRepo.UpdateAsync(held);
            }
            catch (InvalidOperationException)
            {
                _logger.LogDebug(
                    "Held message {HeldId} changed concurrently while auto-rejecting as spam - leaving for a later sweep",
                    held.Id
                );
                return;
            }

            _logger.LogInformation(
                "Auto-rejected held message {HeldId} in {Mailbox} as spam ({Confidence}) from {Sender} - {Subject}. Reason: {Reason}",
                held.Id,
                held.CommitteeEmail,
                held.SpamConfidence,
                !string.IsNullOrWhiteSpace(held.SenderName) ? held.SenderName : held.SenderEmail,
                held.Subject,
                held.SpamReason
            );
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
