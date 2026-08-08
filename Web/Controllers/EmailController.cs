using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Web.Authorization;
using Web.Models;
using Web.PresentationModels;
using Web.Services;
using Web.Services.Repositories;
using Web.UpdateModels;

namespace Web.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class EmailController : ControllerBase
    {
        private readonly ICurrentUserAccessor _currentUser;
        private readonly IHomeRepository _homeRepository;
        private readonly IResidentRepository _residentRepository;
        private readonly IAuditLogRepository _auditLogRepository;
        private readonly IEmailJobRepository _emailJobRepository;
        private readonly IDocumentFileStore _fileStore;
        private readonly EmailJobQueue _emailJobQueue;
        private readonly EmailJobProcessor _emailJobProcessor;
        private readonly EmailJobCleanupService _emailJobCleanup;
        private readonly IEmailDeliveryEventRepository _deliveryEventRepository;
        private readonly IUnsubscribeLinkIssuer _unsubscribeLinkIssuer;
        private readonly string _appBaseUrl;
        private readonly ILogger<EmailController> _logger;

        public EmailController(
            ICurrentUserAccessor currentUser,
            IHomeRepository homeRepository,
            IResidentRepository residentRepository,
            IAuditLogRepository auditLogRepository,
            IEmailJobRepository emailJobRepository,
            IDocumentFileStore fileStore,
            EmailJobQueue emailJobQueue,
            EmailJobProcessor emailJobProcessor,
            EmailJobCleanupService emailJobCleanup,
            IEmailDeliveryEventRepository deliveryEventRepository,
            IUnsubscribeLinkIssuer unsubscribeLinkIssuer,
            IConfiguration config,
            ILogger<EmailController> logger
        )
        {
            _currentUser = currentUser;
            _homeRepository = homeRepository;
            _residentRepository = residentRepository;
            _auditLogRepository = auditLogRepository;
            _emailJobRepository = emailJobRepository;
            _fileStore = fileStore;
            _emailJobQueue = emailJobQueue;
            _emailJobProcessor = emailJobProcessor;
            _emailJobCleanup = emailJobCleanup;
            _deliveryEventRepository = deliveryEventRepository;
            _unsubscribeLinkIssuer = unsubscribeLinkIssuer;
            _appBaseUrl = (config["AppBaseUrl"] ?? "").TrimEnd('/');
            _logger = logger;
        }

        // ──────────────────────────────────────────────
        // Committee email send endpoints (now async)
        // ──────────────────────────────────────────────

        [HttpPut("from-board")]
        [Authorize(Policy = "Board")]
        public Task<IActionResult> SendEmailFromBoard([FromBody] EmailInfo emailInfo) =>
            SendCommitteeEmail(
                "board@cohad.org",
                "COHAD Board",
                emailInfo,
                e => e != null && e.BoardEmailOptedIn,
                "board",
                "Board"
            );

        [HttpPut("from-welcome")]
        [Authorize(Policy = "WelcomeCommittee")]
        public Task<IActionResult> SendEmailFromWelcomeCommittee([FromBody] EmailInfo emailInfo) =>
            SendCommitteeEmail(
                "welcome@cohad.org",
                "COHAD Welcome Committee",
                emailInfo,
                e => e != null && e.WelcomeEmailOptedIn,
                "welcome",
                "Welcome Committee"
            );

        [HttpPut("from-garden")]
        [Authorize(Policy = "GardenClub")]
        public Task<IActionResult> SendEmailFromGardenClub([FromBody] EmailInfo emailInfo) =>
            SendCommitteeEmail(
                "gardenclub@cohad.org",
                "COHAD Garden Club",
                emailInfo,
                e => e != null && e.GardenClubEmailOptedIn,
                "garden",
                "Garden Club"
            );

        [HttpPut("from-social")]
        [Authorize(Policy = "SocialCommittee")]
        public Task<IActionResult> SendEmailFromSocialCommittee([FromBody] EmailInfo emailInfo) =>
            SendCommitteeEmail(
                "social@cohad.org",
                "COHAD Social Committee",
                emailInfo,
                e => e != null && e.SocialCommitteeEmailOptedIn,
                "social",
                "Social Committee"
            );

        [HttpPut("from-sunshine")]
        [Authorize(Policy = "SunshineCommittee")]
        public Task<IActionResult> SendEmailFromSunshineCommittee([FromBody] EmailInfo emailInfo) =>
            SendCommitteeEmail(
                "sunshine@cohad.org",
                "COHAD Sunshine Committee",
                emailInfo,
                e => e != null && e.SunshineCommitteeEmailOptedIn,
                "sunshine",
                "Sunshine Committee"
            );

        // ──────────────────────────────────────────────
        // Job management endpoints
        // ──────────────────────────────────────────────

        [HttpGet("test-recipients")]
        [Authorize(Policy = "EmailSender")]
        public async Task<IActionResult> GetTestRecipients()
        {
            var apiUser = await _currentUser.GetAsync(User);
            if (apiUser == null)
                return Unauthorized(new { error = "User not found." });

            var allowed = await GetEmailsForUserHomes(apiUser);
            var result = allowed
                .Values.Select(r => new TestRecipientOption { Email = r.Email, HomeId = r.HomeId })
                .ToList();

            return Ok(result);
        }

        [HttpGet("jobs")]
        [Authorize(Policy = "EmailSender")]
        public async Task<IActionResult> GetRecentJobs([FromQuery] int limit = 50)
        {
            var jobs = await _emailJobRepository.GetRecentJobsAsync(Math.Clamp(limit, 1, 100));
            return Ok(jobs.Select(EmailJobSummary.FromJob));
        }

        [HttpGet("jobs/{id:guid}")]
        [Authorize(Policy = "EmailSender")]
        public async Task<IActionResult> GetJob(Guid id)
        {
            var job = await _emailJobRepository.GetByIdAsync(id);
            if (job == null)
                return NotFound();

            return Ok(EmailJobDetail.FromJob(job));
        }

        [HttpGet("jobs/{id:guid}/delivery-events")]
        [Authorize(Policy = "Administrator")]
        public async Task<IActionResult> GetJobDeliveryEvents(Guid id, [FromQuery] bool includePayload = false)
        {
            var job = await _emailJobRepository.GetByIdAsync(id);
            if (job == null)
                return NotFound();

            var events = await _deliveryEventRepository.GetByJobIdAsync(id);
            var dtos = events
                .OrderBy(e => e.Email, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.ReceivedUtc)
                .Select(e => EmailDeliveryEventDetail.FromEvent(e, includePayload))
                .ToList();

            return Ok(dtos);
        }

        [HttpPost("jobs/{id:guid}/retry")]
        [Authorize(Policy = "EmailSender")]
        public async Task<IActionResult> RetryJob(Guid id)
        {
            // Don't allow retry while the processor is actively working on this job
            if (_emailJobProcessor.IsJobActive(id))
                return Conflict(
                    new { error = "This job is currently being processed. Cancel it first if you want to retry." }
                );

            var job = await _emailJobRepository.GetByIdAsync(id);
            if (job == null)
                return NotFound();

            if (
                job.Status != EmailJobStatus.Failed
                && job.Status != EmailJobStatus.PartiallyCompleted
                && job.Status != EmailJobStatus.Cancelled
            )
            {
                return BadRequest(new { error = $"Cannot retry a job with status '{job.Status}'." });
            }

            // Reset failed recipients to Pending; leave Sent recipients alone
            foreach (var r in (job.Recipients ?? new()).Where(r => r.Status == EmailJobRecipientStatus.Failed))
            {
                r.Status = EmailJobRecipientStatus.Pending;
                r.Error = null;
                r.SentUtc = null;
            }

            // Backfill unsubscribe links a job may be missing. Links are stamped at job creation and
            // the processor only reads them, so a job created before that convention existed - or
            // before AppBaseUrl was configured - would otherwise resend with no unsubscribe
            // mechanism at all. Retry is the right place for the backfill because it is the same
            // seam as creation: a synchronous admin action where failure is a visible error and no
            // send happens. Grouped jobs are excluded because they never carry links; recipients
            // that already have one keep it.
            if (!job.GroupRecipients)
            {
                var issuanceFailure = await IssueUnsubscribeLinksOrErrorAsync(job.Recipients ?? new());
                if (issuanceFailure != null)
                    return issuanceFailure;
            }

            job.FailedCount = 0;
            job.Status = EmailJobStatus.Queued;
            job.LastError = null;
            job.CompletedUtc = null;
            job.StartedUtc = null;
            try
            {
                await _emailJobRepository.UpdateAsync(job);
                await _emailJobQueue.EnqueueAsync(job.Id);
            }
            catch (EmailJobConcurrencyException)
            {
                return Conflict(
                    new { error = "The job was changed by another process. Refresh the page and try again." }
                );
            }

            return Ok(EmailJobSummary.FromJob(job));
        }

        [HttpPost("jobs/{id:guid}/cancel")]
        [Authorize(Policy = "EmailSender")]
        public async Task<IActionResult> CancelJob(Guid id)
        {
            var job = await _emailJobRepository.GetByIdAsync(id);
            if (job == null)
                return NotFound();

            if (job.Status == EmailJobStatus.Completed || job.Status == EmailJobStatus.Cancelled)
                return BadRequest(new { error = $"Cannot cancel a job with status '{job.Status}'." });

            // Signal the processor to stop (if running in-memory)
            _emailJobProcessor.RequestCancellation(id);

            // Re-read to get the authoritative state — the processor may have already
            // completed or updated the job between our initial read and now.
            var current = await _emailJobRepository.GetByIdAsync(id);
            if (current == null)
                return NotFound();

            // Only persist Cancelled if the job is still in an active state
            if (current.Status == EmailJobStatus.Queued || current.Status == EmailJobStatus.InProgress)
            {
                current.Status = EmailJobStatus.Cancelled;
                current.CompletedUtc = DateTime.UtcNow;
                try
                {
                    await _emailJobRepository.UpdateAsync(current);
                }
                catch (EmailJobConcurrencyException)
                {
                    current = await _emailJobRepository.GetByIdAsync(id) ?? current;
                }
            }

            return Ok(EmailJobSummary.FromJob(current));
        }

        // ──────────────────────────────────────────────
        // Private helpers
        // ──────────────────────────────────────────────

        /// <summary>
        /// Queues a job that sends <paramref name="emailInfo"/> as <paramref name="fromEmail"/> to every
        /// address matching <paramref name="recipientFilter"/>. <paramref name="committeeLabel"/> is the
        /// committee's human name ("Board", "Garden Club"), used for the audit entry and to describe the
        /// audience on the job.
        /// </summary>
        private async Task<IActionResult> SendCommitteeEmail(
            string fromEmail,
            string fromDisplay,
            EmailInfo emailInfo,
            Func<EmailAddress, bool> recipientFilter,
            string category,
            string committeeLabel
        )
        {
            var apiUser = await _currentUser.GetAsync(User);
            if (apiUser == null)
                return Unauthorized(new { error = "User not found." });

            // Best-effort retention cleanup (terminal jobs older than configured retention).
            // This runs on submission because emails are typically sent infrequently.
            try
            {
                await _emailJobCleanup.RunOnceBestEffortAsync();
            }
            catch (Exception ex)
            {
                // Best-effort only: never block a send because cleanup failed.
                _logger.LogWarning(ex, "Email job retention cleanup failed during send submission (best-effort).");
            }

            List<EmailJobRecipient> recipients;

            // How this job's audience is described on the job list/detail pages.
            string toDisplay;

            if (emailInfo.IsTestEmail)
            {
                if (emailInfo.TestRecipientEmails == null || emailInfo.TestRecipientEmails.Count == 0)
                    return BadRequest(new { error = "At least one test recipient email is required." });

                // Normalize: deduplicate and trim (case-insensitive)
                var distinctEmails = emailInfo
                    .TestRecipientEmails.Select(e => e?.Trim())
                    .Where(e => !string.IsNullOrEmpty(e))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (distinctEmails.Count == 0)
                    return BadRequest(new { error = "At least one test recipient email is required." });

                // Validate that all requested addresses belong to the sender's own homes
                var allowedRecipients = await GetEmailsForUserHomes(apiUser);
                var invalidEmails = distinctEmails.Where(e => !allowedRecipients.ContainsKey(e)).ToList();
                if (invalidEmails.Count > 0)
                    return BadRequest(
                        new
                        {
                            error = $"These emails are not associated with your home(s): {string.Join(", ", invalidEmails)}",
                        }
                    );

                recipients = distinctEmails.Select(e => allowedRecipients[e]).ToList();
                toDisplay = EmailAudience.TestRecipients;

                // Prefix subject for test emails; carry the normalized list forward
                emailInfo = new EmailInfo
                {
                    Subject = $"Test: {emailInfo.Subject}",
                    HtmlBody = emailInfo.HtmlBody,
                    IsTestEmail = true,
                    TestRecipientEmails = distinctEmails,
                };
            }
            else
            {
                // Resolve recipients (snapshot at job creation time)
                recipients = await GetAllEmailsMatchingFilter(recipientFilter);
                if (recipients.Count == 0)
                    return Ok(new { message = "No recipients matched the filter." });

                toDisplay = EmailAudience.ForCommitteeSend(committeeLabel);
            }

            // Unsubscribe credentials, issued here - at job creation, synchronously, before the job
            // exists - and never by the processor. This placement is the conclusion of three review
            // rounds, so it is worth recording why:
            //
            // The send loop's ordering carries interlocking invariants (the per-recipient attempt
            // budget bounds termination, the persist merges webhook state, the already-Sent skip
            // depends on that merge), and issuance positioned anywhere inside it broke one of them
            // per attempt - an infinite re-queue, then credentials minted for skipped recipients.
            // Here, none of those exist. Failure is a plain error to the admin who clicked Send,
            // with no job created, no partial send to account for, and a retry that is a visible
            // human action rather than a background loop.
            //
            // What this placement costs, accepted knowingly: a failed submission leaves the
            // already-issued rows orphaned (valid but undelivered) until the container TTL prunes
            // them. That is bounded by one send's recipient count, visible to the admin who saw the
            // error, and preferable to any background retry semantics.
            var issuanceFailure = await IssueUnsubscribeLinksOrErrorAsync(recipients);
            if (issuanceFailure != null)
                return issuanceFailure;

            // Create job
            var job = new EmailJob
            {
                Id = Guid.NewGuid(),
                Status = EmailJobStatus.Queued,
                Category = category,
                FromEmail = fromEmail,
                FromDisplay = fromDisplay,
                ToDisplay = toDisplay,
                Subject = emailInfo.Subject,
                CreatedUtc = DateTime.UtcNow,
                CreatedByUserId = apiUser.UniqueId,
                CreatedByDisplayName = $"{apiUser.GivenName ?? ""} {apiUser.Surname ?? ""}".Trim(),
                TotalRecipients = recipients.Count,
                Recipients = recipients,
            };

            // Store HTML body in blob storage
            job.ContentBlobPath = $"email-jobs/{job.Id:D}.html";
            var jobPersisted = false;
            try
            {
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(emailInfo.HtmlBody ?? "")))
                {
                    await _fileStore.UploadAsync(job.ContentBlobPath, stream, "text/html");
                }

                await _emailJobRepository.AddAsync(job);
                jobPersisted = true;
            }
            catch
            {
                // Only delete the blob when the job was NOT persisted; otherwise the
                // processor still needs it and deleting would cause "content not found".
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

            await AuditEmail(committeeLabel, emailInfo, apiUser);

            try
            {
                await _emailJobQueue.EnqueueAsync(job.Id);
            }
            catch
            {
                // Compensate: avoid orphaned Cosmos row pointing at blob if enqueue fails
                try
                {
                    await _emailJobRepository.DeleteAsync(job.Id);
                }
                catch
                { /* best-effort cleanup */
                }
                if (!string.IsNullOrEmpty(job.ContentBlobPath))
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

            return Accepted(EmailJobSummary.FromJob(job));
        }

        /// <summary>
        /// Issues an unsubscribe short link for every recipient that can carry one and does not
        /// already have one, stamping the id on the recipient. The send path only ever reads the
        /// stamped value - see the comment at the send call site for why issuance lives here and
        /// nowhere else. Returns null on success, or the error response to send the caller.
        /// <para>
        /// Recipients with no home or a blank address are skipped, not failed: they have nothing to
        /// link to, the footer builder renders no footer for a null id, and one bad directory record
        /// must not block everyone else's mail. Recipients already carrying an id keep it, which is
        /// what makes the retry-path backfill idempotent.
        /// </para>
        /// <para>
        /// The failure response names the likely cause instead of letting the exception surface as
        /// an anonymous 500 - an admin staring at a generic error retries blindly, and each blind
        /// retry orphans another batch of issued rows. Saying "check the container" once is the
        /// difference between one support step and a loop.
        /// </para>
        /// </summary>
        private async Task<IActionResult> IssueUnsubscribeLinksOrErrorAsync(List<EmailJobRecipient> recipients)
        {
            // No AppBaseUrl means no link can be emitted at all - a configuration state the operator
            // chose, matching the footer builder's own gate, not a fault worth failing a send over.
            if (string.IsNullOrEmpty(_appBaseUrl))
                return null;

            var eligible = recipients
                .Where(r =>
                    r.HomeId != Guid.Empty
                    && !string.IsNullOrWhiteSpace(r.Email)
                    && string.IsNullOrEmpty(r.UnsubscribeLinkId)
                )
                .ToList();

            try
            {
                // Parallel per the repo convention for independent I/O, but in bounded chunks: an
                // association-wide send is a few hundred recipients, and firing every write at once
                // against the shared-throughput container is a self-inflicted 429 burst that would
                // fail the send at exactly the audience size the feature matters most for.
                foreach (var chunk in eligible.Chunk(16))
                {
                    await Task.WhenAll(
                        chunk.Select(async r =>
                        {
                            var link = await _unsubscribeLinkIssuer.IssueAsync(r.HomeId, r.Email);
                            r.UnsubscribeLinkId = link.Id;
                        })
                    );
                }

                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(
                    ex,
                    "Failed to issue unsubscribe links; the send was refused before any mail went out."
                );
                return StatusCode(
                    StatusCodes.Status500InternalServerError,
                    new
                    {
                        error = "Could not issue unsubscribe links, so nothing was sent. "
                            + "Check that the UnsubscribeLink Cosmos container exists, then try again.",
                    }
                );
            }
        }

        private async Task<List<EmailJobRecipient>> GetAllEmailsMatchingFilter(Func<EmailAddress, bool> filter)
        {
            var seen = new Dictionary<string, EmailJobRecipient>(StringComparer.OrdinalIgnoreCase);
            var homes = await _homeRepository.GetAllAsync();
            var allResidents = await _residentRepository.GetAllAsync();
            var residentsByHome = allResidents.GroupBy(r => r.HomeId).ToDictionary(g => g.Key, g => g.ToList());

            foreach (var home in homes)
            {
                if (residentsByHome.TryGetValue(home.Id, out var residents))
                {
                    foreach (var resident in residents)
                    {
                        if (resident.EmailAddresses == null)
                            continue;
                        foreach (var addr in resident.EmailAddresses.Where(filter))
                        {
                            if (!string.IsNullOrWhiteSpace(addr.Address) && !seen.ContainsKey(addr.Address))
                                seen[addr.Address] = new EmailJobRecipient
                                {
                                    Email = addr.Address,
                                    HomeId = home.Id,
                                    Status = EmailJobRecipientStatus.Pending,
                                };
                        }
                    }
                }

                if (filter(home.EmailAddress) && !string.IsNullOrWhiteSpace(home.EmailAddress?.Address))
                {
                    if (!seen.ContainsKey(home.EmailAddress.Address))
                        seen[home.EmailAddress.Address] = new EmailJobRecipient
                        {
                            Email = home.EmailAddress.Address,
                            HomeId = home.Id,
                            Status = EmailJobRecipientStatus.Pending,
                        };
                }
            }

            return seen.Values.ToList();
        }

        /// <summary>
        /// Returns a case-insensitive dictionary of all email addresses associated with the user's homes,
        /// mapped to an <see cref="EmailJobRecipient"/> ready for job creation.
        /// </summary>
        private async Task<Dictionary<string, EmailJobRecipient>> GetEmailsForUserHomes(User apiUser)
        {
            var homeIds = apiUser.OwnedHomeIds ?? new List<Guid>();
            var result = new Dictionary<string, EmailJobRecipient>(StringComparer.OrdinalIgnoreCase);
            if (homeIds.Count == 0)
            {
                // A sender with no associated home (e.g. an Administrator) still needs to be able to
                // test-send. Fall back to their own account email(s) so the test picker isn't empty
                // and the test-send validation accepts them. Emails may hold several
                // comma/semicolon-separated addresses, so split rather than using the raw string.
                // HomeId is Guid.Empty because these are not subscription recipients — unsubscribe
                // headers should be suppressed for them.
                foreach (var accountEmail in UserEmailHelpers.SplitEmails(apiUser.Emails))
                {
                    if (!result.ContainsKey(accountEmail))
                    {
                        result[accountEmail] = new EmailJobRecipient
                        {
                            Email = accountEmail,
                            HomeId = Guid.Empty,
                            Status = EmailJobRecipientStatus.Pending,
                        };
                    }
                }

                return result;
            }

            var homes = await _homeRepository.GetByIdsAsync(homeIds);
            var allResidents = await _residentRepository.GetByHomeIdsAsync(homeIds);
            var residentsByHome = allResidents.GroupBy(r => r.HomeId).ToDictionary(g => g.Key, g => g.ToList());

            foreach (var home in homes)
            {
                var homeEmail = home.EmailAddress?.Address?.Trim();
                if (!string.IsNullOrWhiteSpace(homeEmail) && !result.ContainsKey(homeEmail))
                    result[homeEmail] = new EmailJobRecipient
                    {
                        Email = homeEmail,
                        HomeId = home.Id,
                        Status = EmailJobRecipientStatus.Pending,
                    };

                if (residentsByHome.TryGetValue(home.Id, out var residents))
                {
                    foreach (var resident in residents)
                    {
                        if (resident.EmailAddresses == null)
                            continue;
                        foreach (var addr in resident.EmailAddresses)
                        {
                            var email = addr?.Address?.Trim();
                            if (!string.IsNullOrWhiteSpace(email) && !result.ContainsKey(email))
                                result[email] = new EmailJobRecipient
                                {
                                    Email = email,
                                    HomeId = home.Id,
                                    Status = EmailJobRecipientStatus.Pending,
                                };
                        }
                    }
                }
            }

            return result;
        }

        private async Task AuditEmail(string from, EmailInfo emailInfo, User apiUser)
        {
            var recipientDesc = emailInfo.IsTestEmail
                ? $"Test: {string.Join(", ", emailInfo.TestRecipientEmails ?? new List<string>())}"
                : "Neighborhood";

            await _auditLogRepository.AddAsync(
                new NewAuditLogEntry
                {
                    Id = Guid.NewGuid(),
                    SubjectId = "",
                    SubjectName = $"Email recipient: {recipientDesc}",
                    Action = $"Sent email from {from}",
                    Time = DateTime.UtcNow,
                    UserDisplayName = $"{apiUser.GivenName ?? ""} {apiUser.Surname ?? ""}",
                    UserId = apiUser.UniqueId,
                }
            );
        }
    }
}
