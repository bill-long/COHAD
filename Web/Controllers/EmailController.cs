using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
        private readonly IUserRepository _userRepository;
        private readonly IHomeRepository _homeRepository;
        private readonly IAuditLogRepository _auditLogRepository;
        private readonly IEmailJobRepository _emailJobRepository;
        private readonly IDocumentFileStore _fileStore;
        private readonly EmailJobQueue _emailJobQueue;
        private readonly EmailJobProcessor _emailJobProcessor;
        private readonly IEmailService _emailService;

        public EmailController(
            IUserRepository userRepository,
            IHomeRepository homeRepository,
            IAuditLogRepository auditLogRepository,
            IEmailJobRepository emailJobRepository,
            IDocumentFileStore fileStore,
            EmailJobQueue emailJobQueue,
            EmailJobProcessor emailJobProcessor,
            IEmailService emailService)
        {
            _userRepository = userRepository;
            _homeRepository = homeRepository;
            _auditLogRepository = auditLogRepository;
            _emailJobRepository = emailJobRepository;
            _fileStore = fileStore;
            _emailJobQueue = emailJobQueue;
            _emailJobProcessor = emailJobProcessor;
            _emailService = emailService;
        }

        // ──────────────────────────────────────────────
        // Committee email send endpoints (now async)
        // ──────────────────────────────────────────────

        [HttpPut("from-board")]
        [Authorize(Policy = "Board")]
        public Task<IActionResult> SendEmailFromBoard([FromBody] EmailInfo emailInfo)
            => SendCommitteeEmail("board@cohad.org", "COHAD Board", emailInfo,
                e => e != null && e.BoardEmailOptedIn, "board", "Board");

        [HttpPut("from-welcome")]
        [Authorize(Policy = "WelcomeCommittee")]
        public Task<IActionResult> SendEmailFromWelcomeCommittee([FromBody] EmailInfo emailInfo)
            => SendCommitteeEmail("welcome@cohad.org", "COHAD Welcome Committee", emailInfo,
                e => e != null && e.WelcomeEmailOptedIn, "welcome", "Welcome Committee");

        [HttpPut("from-garden")]
        [Authorize(Policy = "GardenClub")]
        public Task<IActionResult> SendEmailFromGardenClub([FromBody] EmailInfo emailInfo)
            => SendCommitteeEmail("gardenclub@cohad.org", "COHAD Garden Club", emailInfo,
                e => e != null && e.GardenClubEmailOptedIn, "garden", "Garden Club");

        [HttpPut("from-social")]
        [Authorize(Policy = "SocialCommittee")]
        public Task<IActionResult> SendEmailFromSocialCommittee([FromBody] EmailInfo emailInfo)
            => SendCommitteeEmail("social@cohad.org", "COHAD Social Committee", emailInfo,
                e => e != null && e.SocialCommitteeEmailOptedIn, "social", "Social Committee");

        [HttpPut("from-sunshine")]
        [Authorize(Policy = "SunshineCommittee")]
        public Task<IActionResult> SendEmailFromSunshineCommittee([FromBody] EmailInfo emailInfo)
            => SendCommitteeEmail("sunshine@cohad.org", "COHAD Sunshine Committee", emailInfo,
                e => e != null && e.SunshineCommitteeEmailOptedIn, "sunshine", "Sunshine Committee");

        // ──────────────────────────────────────────────
        // Job management endpoints
        // ──────────────────────────────────────────────

        [HttpGet("jobs")]
        [Authorize(Policy = "Resident")]
        public async Task<IActionResult> GetRecentJobs([FromQuery] int limit = 50)
        {
            var jobs = await _emailJobRepository.GetRecentJobsAsync(Math.Clamp(limit, 1, 100));
            return Ok(jobs.Select(EmailJobSummary.FromJob));
        }

        [HttpGet("jobs/{id:guid}")]
        [Authorize(Policy = "Resident")]
        public async Task<IActionResult> GetJob(Guid id)
        {
            var job = await _emailJobRepository.GetByIdAsync(id);
            if (job == null)
                return NotFound();

            return Ok(EmailJobDetail.FromJob(job));
        }

        [HttpPost("jobs/{id:guid}/retry")]
        [Authorize(Policy = "Resident")]
        public async Task<IActionResult> RetryJob(Guid id)
        {
            // Don't allow retry while the processor is actively working on this job
            if (_emailJobProcessor.IsJobActive(id))
                return Conflict(new { error = "This job is currently being processed. Cancel it first if you want to retry." });

            var job = await _emailJobRepository.GetByIdAsync(id);
            if (job == null)
                return NotFound();

            if (job.Status != EmailJobStatus.Failed &&
                job.Status != EmailJobStatus.PartiallyCompleted &&
                job.Status != EmailJobStatus.Cancelled)
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

            job.FailedCount = 0;
            job.Status = EmailJobStatus.Queued;
            job.LastError = null;
            job.CompletedUtc = null;
            await _emailJobRepository.UpdateAsync(job);
            await _emailJobQueue.EnqueueAsync(job.Id);

            return Ok(EmailJobSummary.FromJob(job));
        }

        [HttpPost("jobs/{id:guid}/cancel")]
        [Authorize(Policy = "Resident")]
        public async Task<IActionResult> CancelJob(Guid id)
        {
            var job = await _emailJobRepository.GetByIdAsync(id);
            if (job == null)
                return NotFound();

            if (job.Status == EmailJobStatus.Completed || job.Status == EmailJobStatus.Cancelled)
                return BadRequest(new { error = $"Cannot cancel a job with status '{job.Status}'." });

            // Signal the processor to stop (if running in-memory)
            var wasCancelled = _emailJobProcessor.RequestCancellation(id);

            if (!wasCancelled)
            {
                // The job may have completed between our status check and the cancel attempt.
                // Re-read to get the current state.
                var current = await _emailJobRepository.GetByIdAsync(id);
                if (current != null && (current.Status == EmailJobStatus.Completed ||
                                        current.Status == EmailJobStatus.PartiallyCompleted ||
                                        current.Status == EmailJobStatus.Failed))
                {
                    return Ok(EmailJobSummary.FromJob(current));
                }
            }

            // Persist the cancelled status (processor will also stop via its CancellationToken)
            job.Status = EmailJobStatus.Cancelled;
            job.CompletedUtc = DateTime.UtcNow;
            await _emailJobRepository.UpdateAsync(job);

            return Ok(EmailJobSummary.FromJob(job));
        }

        // ──────────────────────────────────────────────
        // Private helpers
        // ──────────────────────────────────────────────

        private async Task<IActionResult> SendCommitteeEmail(
            string fromEmail, string fromDisplay, EmailInfo emailInfo,
            Func<EmailAddress, bool> recipientFilter, string category, string auditFrom)
        {
            var apiUser = await _userRepository.GetByUniqueIdAsync(
                Models.User.GetUniqueIdFromClaims(User.Claims));

            // Test emails still use the synchronous path (single recipient, immediate feedback)
            if (emailInfo.IsTestEmail)
            {
                await AuditEmail(auditFrom, emailInfo, apiUser);
                await _emailService.SendEmail(fromEmail, fromDisplay, emailInfo, recipientFilter, category, User);
                return Ok();
            }

            // Resolve recipients (snapshot at job creation time)
            var recipients = await GetAllEmailsMatchingFilter(recipientFilter);
            if (recipients.Count == 0)
                return Ok(new { message = "No recipients matched the filter." });

            // Create job
            var job = new EmailJob
            {
                Id = Guid.NewGuid(),
                Status = EmailJobStatus.Queued,
                Category = category,
                FromEmail = fromEmail,
                FromDisplay = fromDisplay,
                Subject = emailInfo.Subject,
                CreatedUtc = DateTime.UtcNow,
                CreatedByUserId = apiUser.UniqueId,
                CreatedByDisplayName = $"{apiUser.GivenName ?? ""} {apiUser.Surname ?? ""}".Trim(),
                TotalRecipients = recipients.Count,
                Recipients = recipients
            };

            // Store HTML body in blob storage
            job.ContentBlobPath = $"email-jobs/{job.Id:D}.html";
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(emailInfo.HtmlBody ?? "")))
            {
                await _fileStore.UploadAsync(job.ContentBlobPath, stream, "text/html");
            }

            // Persist job and enqueue for processing
            await _emailJobRepository.AddAsync(job);
            await _emailJobQueue.EnqueueAsync(job.Id);

            await AuditEmail(auditFrom, emailInfo, apiUser);

            return Accepted(EmailJobSummary.FromJob(job));
        }

        private async Task<List<EmailJobRecipient>> GetAllEmailsMatchingFilter(Func<EmailAddress, bool> filter)
        {
            var seen = new Dictionary<string, EmailJobRecipient>(StringComparer.OrdinalIgnoreCase);
            var homes = await _homeRepository.GetAllAsync();

            foreach (var home in homes)
            {
                if (home.Residents != null)
                {
                    foreach (var resident in home.Residents)
                    {
                        if (resident.EmailAddresses == null) continue;
                        foreach (var addr in resident.EmailAddresses.Where(filter))
                        {
                            if (!string.IsNullOrWhiteSpace(addr.Address) && !seen.ContainsKey(addr.Address))
                                seen[addr.Address] = new EmailJobRecipient
                                {
                                    Email = addr.Address,
                                    HomeId = home.Id,
                                    Status = EmailJobRecipientStatus.Pending
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
                            Status = EmailJobRecipientStatus.Pending
                        };
                }
            }

            return seen.Values.ToList();
        }

        private async Task AuditEmail(string from, EmailInfo emailInfo, User apiUser)
        {
            await _auditLogRepository.AddAsync(new NewAuditLogEntry
            {
                Id = Guid.NewGuid(),
                SubjectId = "",
                SubjectName = $"Email recipient: {(emailInfo.IsTestEmail ? apiUser.Emails : "Neighborhood")}",
                Action = $"Sent email from {from}",
                Time = DateTime.UtcNow,
                UserDisplayName = $"{apiUser.GivenName ?? ""} {apiUser.Surname ?? ""}",
                UserId = apiUser.UniqueId
            });
        }
    }
}
