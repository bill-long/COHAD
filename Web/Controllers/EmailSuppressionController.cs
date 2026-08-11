using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
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
        /// fighting the admin. COHAD's clear happens first; a failed provider call is surfaced as
        /// <see cref="ClearEmailSuppressionResponseDto.ProviderWarning"/> rather than failing the
        /// request, because the reconciliation bounds the inconsistency and Clear can be retried.
        /// The provider call also runs on the idempotent already-cleared path - that retry is the
        /// recovery route the warning points at, and the provider-side delete is itself a no-op
        /// when the entry is already gone.
        /// </para>
        /// </summary>
        [HttpPost("{id}/clear")]
        public async Task<IActionResult> Clear(string id)
        {
            var apiUser = await _currentUser.GetAsync(User);

            SuppressionClearOutcome outcome;
            try
            {
                outcome = await _service.ClearByIdAsync(id, apiUser.UniqueId);
            }
            catch (ConcurrencyConflictException)
            {
                return Conflict(new { error = "The record is being updated concurrently. Please try again." });
            }

            if (outcome.Suppression == null)
                return NotFound(new { error = "No suppression with this id." });

            string providerWarning = null;
            string providerAuditNote = "";
            if (outcome.Suppression.Reason == SuppressionReason.ProviderUnsubscribe)
            {
                var reactivation = await _reactivationService.ReactivateAsync(
                    outcome.Suppression.Email,
                    HttpContext.RequestAborted
                );
                if (reactivation.Succeeded)
                {
                    providerAuditNote = " Also reactivated the address at the email provider.";
                }
                else
                {
                    providerWarning =
                        "The suppression is cleared here, but reactivating the address at the email"
                        + " provider failed - mail to it may still be silently dropped, and the"
                        + " periodic reconciliation may re-suppress it. Show cleared suppressions"
                        + " and use Retry provider reactivation to try the provider call again.";
                    providerAuditNote =
                        " Reactivating the address at the email provider failed; it may still be suppressed there.";
                }
            }

            // Audited only when this call did the clearing - an idempotent no-op recording "X
            // cleared the suppression" would attribute the action to someone who took none. A
            // retry that only repeats the provider call is deliberately not audited either: the
            // provider reports "Deleted" for an entry that never existed, so there is no telling
            // a repair from a no-op.
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
                            + providerAuditNote,
                    }
                );
            }

            return Ok(
                new ClearEmailSuppressionResponseDto
                {
                    Suppression = EmailSuppressionDto.FromModel(outcome.Suppression),
                    ProviderWarning = providerWarning,
                }
            );
        }
    }
}
