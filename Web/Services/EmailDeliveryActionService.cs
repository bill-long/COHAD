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

        /// <summary>
        /// Processes a Postmark SubscriptionChange webhook event: an address was added to or
        /// removed from a Postmark message stream's suppression list. A recipient unsubscribe at
        /// the Postmark layer (a mail client's native Unsubscribe button on the broadcast
        /// stream's List-Unsubscribe) never touches COHAD's own unsubscribe link, so without
        /// this writer the address keeps "successfully" mailing into Postmark's void.
        /// <para>
        /// Only Postmark's <c>ManualSuppression</c> reason records here - hard-bounce and
        /// spam-complaint subscription changes also fire this webhook, but the Bounce and
        /// SpamComplaint webhooks already write those suppressions with truthful provenance.
        /// A reactivation (<paramref name="suppressSending"/> false) clears only a
        /// <see cref="SuppressionReason.ProviderUnsubscribe"/> record - never a bounce,
        /// complaint, resident-request, or admin suppression.
        /// </para>
        /// </summary>
        Task<SubscriptionChangeAction> ProcessSubscriptionChangeAsync(
            string email,
            bool suppressSending,
            string? origin,
            string? postmarkSuppressionReason,
            string? messageStream,
            Guid? causingJobId,
            string evidenceKey
        );
    }

    /// <summary>
    /// What a <see cref="IEmailDeliveryActionService.ProcessSubscriptionChangeAsync"/> call did.
    /// The webhook controller stores a delivery event only for <see cref="Suppressed"/>, so the
    /// bounce/complaint skip rule lives in exactly one place (the service).
    /// </summary>
    public enum SubscriptionChangeAction
    {
        /// <summary>Nothing changed: a bounce/complaint-origin change, or a no-op reactivation.</summary>
        None = 0,

        /// <summary>A suppression is in force for the address (recorded or already recorded).</summary>
        Suppressed = 1,

        /// <summary>A ProviderUnsubscribe suppression was lifted by this call.</summary>
        Cleared = 2,
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

        public async Task<SubscriptionChangeAction> ProcessSubscriptionChangeAsync(
            string email,
            bool suppressSending,
            string? origin,
            string? postmarkSuppressionReason,
            string? messageStream,
            Guid? causingJobId,
            string evidenceKey
        )
        {
            var diagnostic = BuildSubscriptionChangeDiagnostic(origin, postmarkSuppressionReason, messageStream);

            if (suppressSending)
            {
                // Postmark fires SubscriptionChange for hard bounces and spam complaints too
                // (they land on the same stream suppression list), but the Bounce and
                // SpamComplaint webhooks already write those suppressions with truthful
                // provenance - recording ProviderUnsubscribe for them would lie about why.
                // Everything else with SuppressSending true (ManualSuppression today, any
                // future value) means "do not mail", which is the safe direction to record.
                if (postmarkSuppressionReason is "HardBounce" or "SpamComplaint")
                {
                    _logger.LogDebug(
                        "Postmark SubscriptionChange for {Email} has reason {Reason} - handled by the delivery-event path, skipping.",
                        email,
                        postmarkSuppressionReason
                    );
                    return SubscriptionChangeAction.None;
                }

                _logger.LogInformation(
                    "Suppressing email {Email} due to Postmark-layer unsubscribe ({Diagnostic}).",
                    email,
                    diagnostic
                );

                // The evidence key dedups webhook retries: Postmark reposts the same payload,
                // and without the key each retry would count as fresh evidence and re-audit.
                var outcome = await _suppressionService.RecordAsync(
                    email,
                    SuppressionReason.ProviderUnsubscribe,
                    EmailSuppression.PostmarkSubscriptionChange,
                    causingJobId,
                    diagnostic,
                    evidenceKey
                );

                // A replay changed nothing, so there is nothing to audit - but the suppression
                // IS in force, so the caller still stores its delivery event (its first attempt
                // may have crashed between the record write and the event store).
                if (!outcome.Applied)
                    return SubscriptionChangeAction.Suppressed;

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
                            $"Suppressed all email due to unsubscribe via the email provider ({diagnostic})."
                            + $" Evidence count {outcome.Suppression.ConsecutiveFailureCount}."
                            + " Opt-in preferences were not changed.",
                    }
                );
                return SubscriptionChangeAction.Suppressed;
            }

            // Reactivation: someone removed the address from Postmark's suppression list. Mirror
            // it, but only for records this same path created - onlyIfReason is enforced at the
            // point of the write, so a bounce, resident-request, or admin suppression can never
            // be lifted by activity in the Postmark dashboard.
            var clearOutcome = await _suppressionService.ClearAsync(
                email,
                EmailSuppression.PostmarkSubscriptionChange,
                onlyIfReason: SuppressionReason.ProviderUnsubscribe
            );

            if (!clearOutcome.Cleared)
                return SubscriptionChangeAction.None;

            _logger.LogInformation(
                "Cleared ProviderUnsubscribe suppression for {Email} after Postmark reactivation.",
                email
            );

            var clearedRedacted = RedactEmail(email);
            await _auditLogRepository.AddAsync(
                new NewAuditLogEntry
                {
                    Id = Guid.NewGuid(),
                    Time = DateTime.UtcNow,
                    UserId = "system",
                    UserDisplayName = "System (auto)",
                    SubjectId = clearedRedacted,
                    SubjectName = clearedRedacted,
                    Action =
                        $"Cleared suppression after the email provider reactivated the address "
                        + $"({messageStream ?? "unknown"} stream).",
                }
            );
            return SubscriptionChangeAction.Cleared;
        }

        /// <summary>
        /// The one composition of the provider-provenance diagnostic for a subscription change,
        /// shared by the suppression record and the stored delivery event so the two can never
        /// drift on wording.
        /// </summary>
        internal static string BuildSubscriptionChangeDiagnostic(
            string? origin,
            string? postmarkSuppressionReason,
            string? messageStream
        )
        {
            return
                $"Postmark stream suppression ({messageStream ?? "unknown"} stream; "
                + $"Origin: {origin ?? "unknown"}; SuppressionReason: {postmarkSuppressionReason ?? "unknown"})";
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
