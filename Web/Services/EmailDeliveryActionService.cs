#nullable enable
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Web.Models;
using Web.Services.Repositories;

namespace Web.Services
{
    public interface IEmailDeliveryActionService
    {
        /// <summary>
        /// Processes a delivery event and takes automatic action: a hard bounce or spam complaint
        /// writes a suppression for the address. Takes the whole event because the suppression
        /// record keeps the event's provenance - the causing job and the provider's own
        /// diagnostic text.
        /// </summary>
        Task ProcessDeliveryEventAsync(EmailDeliveryEvent deliveryEvent, string? category);
    }

    /// <summary>
    /// The provider-feedback writer of the suppression list (the other writer is the one-click
    /// endpoint in <c>UnsubscribeController</c>). Writes a suppression and NOTHING else - in
    /// particular it never touches the five per-address opt-in booleans, which belong to the
    /// resident. The previous behaviour cleared all five across every home carrying the address,
    /// which destroyed stated preferences to record a fact that is not a preference and left
    /// nothing to restore. See docs/email-suppression-and-unsubscribe.md, Part 3.
    /// </summary>
    public class EmailDeliveryActionService : IEmailDeliveryActionService
    {
        private readonly IEmailSuppressionService _suppressionService;
        private readonly IAuditLogRepository _auditLogRepository;
        private readonly ILogger<EmailDeliveryActionService> _logger;

        public EmailDeliveryActionService(
            IEmailSuppressionService suppressionService,
            IAuditLogRepository auditLogRepository,
            ILogger<EmailDeliveryActionService> logger
        )
        {
            _suppressionService = suppressionService;
            _auditLogRepository = auditLogRepository;
            _logger = logger;
        }

        public async Task ProcessDeliveryEventAsync(EmailDeliveryEvent deliveryEvent, string? category)
        {
            var status = deliveryEvent.DeliveryStatus;
            var email = deliveryEvent.Email;

            // Soft bounces arrive as Deferred (the webhook controllers map Postmark's
            // Transient/SoftBounce there), so they never reach the suppression below. A future
            // N-soft-bounces-in-a-window rule would build on the record's
            // ConsecutiveFailureCount; nothing acts on soft bounces today, deliberately.
            if (status != DeliveryStatus.Bounced && status != DeliveryStatus.SpamReport)
            {
                _logger.LogDebug("Delivery event {Status} for {Email} — no auto-action required.", status, email);
                return;
            }

            var reason = status == DeliveryStatus.Bounced ? SuppressionReason.HardBounce : SuppressionReason.SpamComplaint;
            var reasonText = status == DeliveryStatus.Bounced ? "hard bounce" : "spam report";
            _logger.LogInformation("Suppressing email {Email} due to {Reason}.", email, reasonText);

            // The event's deduplicated id is the evidence key. This method is called at-least-once
            // per event - marking the event processed happens after this call and can fail - and
            // without the key each replay would count as fresh evidence and re-audit.
            var outcome = await _suppressionService.RecordAsync(
                email,
                reason,
                EmailSuppression.SystemDeliveryEvent,
                deliveryEvent.JobId == Guid.Empty ? null : deliveryEvent.JobId,
                deliveryEvent.ProviderDiagnostic,
                evidenceKey: deliveryEvent.Id
            );

            // A replayed event changed nothing, so there is nothing to audit - a second
            // "Suppressed all email due to hard bounce" line for one bounce would misstate how
            // often the address failed.
            if (!outcome.Applied)
                return;

            // The suppression record is the durable state; the audit entry is the episode history
            // that survives the record being updated by later evidence or cleared. Redacted like
            // every address in the audit log.
            var redacted = RedactEmail(email);
            await _auditLogRepository.AddAsync(
                new NewAuditLogEntry
                {
                    Id = Guid.NewGuid(),
                    Time = DateTime.UtcNow,
                    UserId = "system",
                    UserDisplayName = "System (auto)",
                    SubjectId = redacted,
                    SubjectName = redacted,
                    Action =
                        $"Suppressed all email due to {reasonText}{(category != null ? $" (category: {category})" : "")}."
                        + $" Evidence count {outcome.Suppression.ConsecutiveFailureCount}. Opt-in preferences were not changed.",
                }
            );
        }

        /// <summary>Redacts an address for the audit log (first 3 chars + domain).</summary>
        internal static string RedactEmail(string email)
        {
            var atIndex = email.IndexOf('@');
            if (atIndex <= 0)
                return "***";
            var localPart = email[..atIndex];
            var domain = email[atIndex..];
            var visible = localPart.Length <= 3 ? localPart : localPart[..3];
            return visible + "***" + domain;
        }
    }
}
