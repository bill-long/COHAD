using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Web.Models;
using Web.PresentationModels;
using Web.Services;
using Web.Services.Repositories;

namespace Web.Controllers
{
    /// <summary>
    /// The Administrator surface over the suppression list: list, suppress by hand, and clear.
    /// A suppression that only a Cosmos query can explain rebuilds the original problem one layer
    /// down - the bounce is recorded and still nobody hears about it - so the records go on
    /// screen with every self-explaining field they carry.
    /// </summary>
    [Route("api/email-suppressions")]
    [ApiController]
    [Authorize(Policy = "Administrator")]
    public class EmailSuppressionController : ControllerBase
    {
        private readonly IEmailSuppressionRepository _repository;
        private readonly IEmailSuppressionService _service;
        private readonly IAuditLogRepository _auditLogRepository;
        private readonly ICurrentUserAccessor _currentUser;
        private readonly IPostmarkReactivationService _reactivationService;

        public EmailSuppressionController(
            IEmailSuppressionRepository repository,
            IEmailSuppressionService service,
            IAuditLogRepository auditLogRepository,
            ICurrentUserAccessor currentUser,
            IPostmarkReactivationService reactivationService
        )
        {
            _repository = repository;
            _service = service;
            _auditLogRepository = auditLogRepository;
            _currentUser = currentUser;
            _reactivationService = reactivationService;
        }

        /// <summary>
        /// Lists suppressions, newest first. Active only by default; <c>includeCleared</c> adds
        /// the history rows, so a restored address reads as a history rather than as an absence.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> List([FromQuery] bool includeCleared = false)
        {
            var records = includeCleared ? await _repository.GetAllAsync() : await _repository.GetActiveAsync();

            return Ok(
                records
                    .OrderByDescending(s => s.SuppressedUtc)
                    .Select(EmailSuppressionDto.FromModel)
                    .ToList()
            );
        }

        /// <summary>
        /// Suppresses an address by hand (<see cref="SuppressionReason.AdminAction"/>) - the
        /// "resident phoned the board" path. Without this endpoint that enum value would be
        /// unreachable and the mailto recovery route would dead-end at a Cosmos query.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> Create([FromBody] CreateEmailSuppressionDto dto)
        {
            // The same bar HomeController applies to stored addresses: contains an '@'. This is
            // an admin tool, not a mail gateway - the address just has to be matchable against
            // directory records, which is all the enforcement point does with it.
            if (string.IsNullOrWhiteSpace(dto?.Email) || !dto.Email.Contains('@'))
                return BadRequest(new { error = "A valid email address is required." });

            var apiUser = await _currentUser.GetAsync(User);

            SuppressionRecordOutcome outcome;
            try
            {
                outcome = await _service.RecordAsync(
                    dto.Email,
                    SuppressionReason.AdminAction,
                    apiUser.UniqueId,
                    null,
                    null
                );
            }
            catch (ConcurrencyConflictException)
            {
                // Every retry lost its race - contention with a webhook or another admin, not a
                // fault. 409 tells the UI "try again" without paging anyone.
                return Conflict(new { error = "The record is being updated concurrently. Please try again." });
            }

            var suppression = outcome.Suppression;
            await _auditLogRepository.AddAsync(
                new NewAuditLogEntry
                {
                    Id = Guid.NewGuid(),
                    Time = DateTime.UtcNow,
                    UserId = apiUser.UniqueId,
                    UserDisplayName = $"{apiUser.GivenName ?? ""} {apiUser.Surname ?? ""}",
                    SubjectId = EmailDeliveryActionService.RedactEmail(suppression.Email),
                    SubjectName = EmailDeliveryActionService.RedactEmail(suppression.Email),
                    Action = $"Suppressed all email (admin action). Evidence count {suppression.ConsecutiveFailureCount}.",
                }
            );

            return Ok(EmailSuppressionDto.FromModel(suppression));
        }

        /// <summary>
        /// Clears a suppression so mail can flow again. Idempotent: clearing an already-cleared
        /// record returns it unchanged with 200 - "make sure this is cleared" is satisfied either
        /// way, and a 409 would force the UI to special-case a no-op.
        /// <para>
        /// By document id, not by address, because this endpoint acts on a listed row: a
        /// hand-authored document whose id does not match <c>MakeId</c> of its own Email is
        /// unreachable through the address-keyed path and must still be clearable by the human
        /// looking at it. The audit decision comes from the service's outcome - whether THIS call
        /// performed the transition - not from a pre-read that a concurrent admin can make stale.
        /// </para>
        /// <para>
        /// Clearing a <see cref="SuppressionReason.ProviderUnsubscribe"/> record also reactivates
        /// the address at the email provider (issue #11): Postmark keeps its own stream
        /// suppression entry, so lifting only COHAD's record would resume "successful" sends the
        /// provider silently drops - and the nightly reconciliation would re-suppress the address,
        /// fighting the admin. The provider reactivation runs FIRST, and a failure fails the
        /// request with the record unchanged (the UnsubscribeLink send-gate philosophy: a
        /// synchronous, human-visible error whose whole recovery is clicking Clear again once the
        /// provider is reachable). The inverse never exists: COHAD cannot show a
        /// provider-unsubscribe record as cleared while the provider still drops its mail, which
        /// no warning banner or reconciliation interval can promise. A provider-side delete that
        /// succeeds but loses the local write race leaves mail suppressed here - the safe
        /// direction - and the retried Clear's provider call is a no-op.
        /// </para>
        /// <para>
        /// The request body's <c>SuppressedUtc</c> is the episode the admin's page displayed
        /// (every re-suppression resets it), checked against the pre-read here and enforced
        /// again at write time by the service - a record re-suppressed between page load and
        /// the write (a fresh bounce, a new unsubscribe) answers 409 instead of being lifted on
        /// stale information, whatever its new reason.
        /// </para>
        /// </summary>
        [HttpPost("{id}/clear")]
        public async Task<IActionResult> Clear(
            string id,
            // EmptyBodyBehavior.Allow, explicitly: [ApiController] otherwise 400s a missing body
            // at model binding, and the episode stamp is deliberately optional (older or non-UI
            // callers simply skip the guard). Stated on the attribute rather than via a nullable
            // annotation because controllers here stay nullable-oblivious - on an MVC action,
            // nullability is binding semantics, not documentation.
            [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)]
                ClearEmailSuppressionRequestDto request = null
        )
        {
            // Independent reads (user store and suppression store), so the interactive request
            // does not pay two round-trips in series before the provider call it already waits
            // out.
            var apiUserTask = _currentUser.GetAsync(User);
            var existingTask = _repository.GetByIdAsync(id);
            await Task.WhenAll(apiUserTask, existingTask);
            var apiUser = await apiUserTask;
            var existing = await existingTask;

            if (existing == null)
                return NotFoundNoSuppression();

            // Refused BEFORE the provider call: the admin's page showed a different episode, and
            // provider entries must not be deleted on the strength of a row the admin never saw.
            // Kind-normalized with the service's own rule so a caller-supplied kind cannot
            // manufacture a false mismatch.
            var expectedEpisode = request?.SuppressedUtc;
            if (
                expectedEpisode.HasValue
                && existing.IsActive
                && existing.SuppressedUtc != EmailSuppressionService.AsUtc(expectedEpisode.Value)
            )
            {
                return SuppressionChangedConflict();
            }

            var reactivated = false;
            // The blank-Email guard keeps a hand-authored or corrupt row clearable: a blank
            // address can never be suppressed at the provider, and asking the provider about it
            // would only manufacture an unresolvable failure for the one surface (by-id clear)
            // that exists to act on any listed row.
            if (
                existing.IsActive
                && existing.Reason == SuppressionReason.ProviderUnsubscribe
                && !string.IsNullOrWhiteSpace(existing.Email)
            )
            {
                var reactivation = await _reactivationService.ReactivateAsync(
                    existing.Email,
                    HttpContext.RequestAborted
                );
                if (!reactivation.SkippedNotConfigured)
                {
                    if (!reactivation.Succeeded)
                    {
                        // A PARTIAL failure still deleted real provider entries; that side
                        // effect is audited even though the clear is refused, or the audit log
                        // could not explain why one stream no longer suppresses an address
                        // COHAD still does.
                        if (reactivation.FailedStreams.Count < reactivation.StreamsAttempted)
                        {
                            await AuditProviderChangeWithoutClearAsync(
                                apiUser,
                                existing.Email,
                                $"Reactivated the address at the email provider on {reactivation.StreamsAttempted - reactivation.FailedStreams.Count}"
                                    + $" of {reactivation.StreamsAttempted} streams; the rest failed and the suppression was NOT cleared."
                            );
                        }

                        // 502: the upstream provider call failed, and without it the clear would
                        // resume "successful" sends the provider silently drops. The provider's
                        // own refusal text is included because it distinguishes a retryable
                        // outage from a permanent refusal (a spam-complaint entry only the
                        // recipient can lift) - "try again later" alone would send the admin in
                        // circles on the latter.
                        return StatusCode(
                            StatusCodes.Status502BadGateway,
                            new
                            {
                                error =
                                    $"Could not reactivate {existing.Email} at the email provider, so the"
                                    + $" suppression was not cleared. {reactivation.FailureDetail}",
                            }
                        );
                    }
                    reactivated = true;
                }
                // SkippedNotConfigured: Postmark is disabled or has no server token, so its
                // suppression lists are not in play (neither on the send path nor via the
                // reconciliation, which needs the same token) - nothing to reactivate, nothing
                // blocking the clear.
            }

            SuppressionClearOutcome outcome;
            try
            {
                // The pre-read's episode, unconditionally - also when the pre-read saw the
                // record as cleared: if it is re-suppressed between our read and the service's,
                // the write-time guard must refuse rather than lift an episode nobody saw
                // (the cleared pre-read's stamp can never match a NEW episode's).
                outcome = await _service.ClearByIdAsync(
                    id,
                    apiUser.UniqueId,
                    onlyIfSuppressedUtc: existing.SuppressedUtc
                );
            }
            catch (ConcurrencyConflictException)
            {
                // The provider-side deletions this request performed are real even though the
                // local write kept losing races - same audit obligation as the refused-episode
                // path below.
                if (reactivated)
                {
                    await AuditProviderChangeWithoutClearAsync(
                        apiUser,
                        existing.Email,
                        "Reactivated the address at the email provider, but the suppression record"
                            + " was updated concurrently and was NOT cleared."
                    );
                }
                return Conflict(new { error = "The record is being updated concurrently. Please try again." });
            }

            if (outcome.Suppression == null)
                return NotFoundNoSuppression();

            if (!outcome.Cleared && outcome.Suppression.IsActive)
            {
                // The episode guard refused the write: the record was re-suppressed between our
                // read and the service's. The admin must see the new episode before mail
                // resumes. If this request already deleted provider entries, that side effect is
                // real and gets its own audit line - the audit log must be able to explain why
                // the provider no longer suppresses an address COHAD still does.
                if (reactivated)
                {
                    await AuditProviderChangeWithoutClearAsync(
                        apiUser,
                        outcome.Suppression.Email,
                        "Reactivated the address at the email provider, but the suppression was"
                            + " re-suppressed concurrently and was NOT cleared."
                    );
                }
                return SuppressionChangedConflict();
            }

            // Audited only when this call did the clearing - an idempotent no-op recording "X
            // cleared the suppression" would attribute the action to someone who took none.
            if (outcome.Cleared)
            {
                var suppression = outcome.Suppression;
                await _auditLogRepository.AddAsync(
                    new NewAuditLogEntry
                    {
                        Id = Guid.NewGuid(),
                        Time = DateTime.UtcNow,
                        UserId = apiUser.UniqueId,
                        UserDisplayName = $"{apiUser.GivenName ?? ""} {apiUser.Surname ?? ""}",
                        SubjectId = EmailDeliveryActionService.RedactEmail(suppression.Email),
                        SubjectName = EmailDeliveryActionService.RedactEmail(suppression.Email),
                        Action =
                            $"Cleared an email suppression (was {suppression.Reason}, suppressed {suppression.SuppressedUtc:u})."
                            + (reactivated ? " Also reactivated the address at the email provider." : ""),
                    }
                );
            }

            return Ok(EmailSuppressionDto.FromModel(outcome.Suppression));
        }

        private static NotFoundObjectResult NotFoundNoSuppression() =>
            new(new { error = "No suppression with this id." });

        private static ConflictObjectResult SuppressionChangedConflict() =>
            new(new { error = "The suppression changed since the page was loaded. Review the record and try again." });

        /// <summary>
        /// The audit line for a request that changed provider-side state without clearing the
        /// local record. One writer for the three ways that can happen (partial provider
        /// failure, lost local write races, refused episode guard), so the wording cannot
        /// drift between them.
        /// </summary>
        private Task AuditProviderChangeWithoutClearAsync(User apiUser, string email, string action)
        {
            var redacted = EmailDeliveryActionService.RedactEmail(email);
            return _auditLogRepository.AddAsync(
                new NewAuditLogEntry
                {
                    Id = Guid.NewGuid(),
                    Time = DateTime.UtcNow,
                    UserId = apiUser.UniqueId,
                    UserDisplayName = $"{apiUser.GivenName ?? ""} {apiUser.Surname ?? ""}",
                    SubjectId = redacted,
                    SubjectName = redacted,
                    Action = action,
                }
            );
        }
    }
}
