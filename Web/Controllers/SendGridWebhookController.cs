#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Web.Models;
using Web.Services;
using Web.Services.Repositories;

namespace Web.Controllers
{
    [Route("api/webhooks/sendgrid")]
    [ApiController]
    [AllowAnonymous]
    public class SendGridWebhookController : ControllerBase
    {
        private readonly ISendGridWebhookVerifier _verifier;
        private readonly IEmailJobRepository _emailJobRepository;
        private readonly IEmailDeliveryActionService _deliveryActionService;
        private readonly ILogger<SendGridWebhookController> _logger;

        private static readonly HashSet<string> TrackedEventTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "delivered",
            "bounce",
            "dropped",
            "spamreport",
            "deferred",
        };

        public SendGridWebhookController(
            ISendGridWebhookVerifier verifier,
            IEmailJobRepository emailJobRepository,
            IEmailDeliveryActionService deliveryActionService,
            ILogger<SendGridWebhookController> logger
        )
        {
            _verifier = verifier;
            _emailJobRepository = emailJobRepository;
            _deliveryActionService = deliveryActionService;
            _logger = logger;
        }

        [HttpPost]
        public async Task<IActionResult> HandleEvents()
        {
            string body;
            using (var reader = new StreamReader(Request.Body))
            {
                body = await reader.ReadToEndAsync();
            }

            // Verify signature if configured
            if (_verifier.IsConfigured)
            {
                var signature = Request.Headers["X-Twilio-Email-Event-Webhook-Signature"].FirstOrDefault();
                var timestamp = Request.Headers["X-Twilio-Email-Event-Webhook-Timestamp"].FirstOrDefault();

                if (
                    string.IsNullOrEmpty(signature)
                    || string.IsNullOrEmpty(timestamp)
                    || !_verifier.Verify(body, signature, timestamp)
                )
                {
                    _logger.LogWarning("SendGrid webhook signature verification failed.");
                    return Forbid();
                }
            }

            List<JsonElement>? events;
            try
            {
                events = JsonSerializer.Deserialize<List<JsonElement>>(body);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse SendGrid webhook body.");
                return BadRequest();
            }

            if (events == null)
                return Ok();

            foreach (var evt in events)
            {
                try
                {
                    await ProcessEventAsync(evt);
                }
                catch (Exception ex)
                {
                    // Log but continue — don't let one bad event fail the batch
                    _logger.LogError(ex, "Error processing SendGrid webhook event.");
                }
            }

            return Ok();
        }

        private async Task ProcessEventAsync(JsonElement evt)
        {
            if (!evt.TryGetProperty("event", out var eventTypeProp))
                return;
            var eventType = eventTypeProp.GetString();
            if (string.IsNullOrEmpty(eventType) || !TrackedEventTypes.Contains(eventType))
                return;

            // Extract correlation args
            string? jobIdStr = null;
            string? email = null;

            if (evt.TryGetProperty("cohad_job_id", out var jobIdProp))
                jobIdStr = jobIdProp.GetString();
            if (evt.TryGetProperty("cohad_email", out var emailProp))
                email = emailProp.GetString();

            // SendGrid flattens unique_args to top-level properties in the event payload.
            // If not found at top level, check inside unique_args as a fallback.
            if (string.IsNullOrEmpty(jobIdStr) || string.IsNullOrEmpty(email))
            {
                if (evt.TryGetProperty("unique_args", out var uniqueArgs))
                {
                    if (
                        string.IsNullOrEmpty(jobIdStr) && uniqueArgs.TryGetProperty("cohad_job_id", out var nestedJobId)
                    )
                        jobIdStr = nestedJobId.GetString();
                    if (string.IsNullOrEmpty(email) && uniqueArgs.TryGetProperty("cohad_email", out var nestedEmail))
                        email = nestedEmail.GetString();
                }
            }

            if (string.IsNullOrEmpty(jobIdStr) || string.IsNullOrEmpty(email))
            {
                _logger.LogDebug(
                    "SendGrid event {EventType} missing cohad_job_id or cohad_email — skipping.",
                    eventType
                );
                return;
            }

            if (!Guid.TryParse(jobIdStr, out var jobId))
            {
                _logger.LogWarning("SendGrid event has invalid cohad_job_id: {JobId}", jobIdStr);
                return;
            }

            var deliveryStatus = MapEventToDeliveryStatus(eventType);

            // Store sg_message_id for debugging
            string? sgMessageId = null;
            if (evt.TryGetProperty("sg_message_id", out var sgMsgProp))
                sgMessageId = sgMsgProp.GetString();

            // Update the job recipient
            var job = await _emailJobRepository.GetByIdAsync(jobId);
            if (job == null)
            {
                _logger.LogDebug("SendGrid event for unknown job {JobId} — skipping.", jobId);
                return;
            }

            var recipient = job.Recipients?.FirstOrDefault(r =>
                string.Equals(r.Email, email, StringComparison.OrdinalIgnoreCase)
            );
            if (recipient == null)
            {
                _logger.LogDebug("SendGrid event for job {JobId} — recipient {Email} not found.", jobId, email);
                return;
            }

            // Only update if the new status is "more severe" or it's the first status
            if (ShouldUpdateDeliveryStatus(recipient.DeliveryStatus, deliveryStatus))
            {
                recipient.DeliveryStatus = deliveryStatus;
                recipient.DeliveryStatusUpdatedUtc = DateTime.UtcNow;

                if (!string.IsNullOrEmpty(sgMessageId) && string.IsNullOrEmpty(recipient.ProviderMessageId))
                    recipient.ProviderMessageId = sgMessageId;

                await _emailJobRepository.UpdateAsync(job);
            }

            // Auto opt-out for bounces and spam reports
            if (deliveryStatus == DeliveryStatus.Bounced || deliveryStatus == DeliveryStatus.SpamReport)
            {
                await _deliveryActionService.ProcessDeliveryEventAsync(email, deliveryStatus, job.Category);
            }
        }

        internal static DeliveryStatus MapEventToDeliveryStatus(string eventType)
        {
            return eventType.ToLowerInvariant() switch
            {
                "delivered" => DeliveryStatus.Delivered,
                "bounce" => DeliveryStatus.Bounced,
                "dropped" => DeliveryStatus.Rejected,
                "spamreport" => DeliveryStatus.SpamReport,
                "deferred" => DeliveryStatus.Deferred,
                _ => DeliveryStatus.Unknown,
            };
        }

        /// <summary>
        /// Determines whether the new delivery status should replace the existing one.
        /// Severity order: Unknown &lt; Deferred &lt; Delivered &lt; Rejected &lt; SpamReport &lt; Bounced.
        /// </summary>
        internal static bool ShouldUpdateDeliveryStatus(DeliveryStatus current, DeliveryStatus incoming)
        {
            if (current == DeliveryStatus.Unknown)
                return true;
            // Already terminal — don't downgrade
            if (
                current == DeliveryStatus.Bounced
                || current == DeliveryStatus.SpamReport
                || current == DeliveryStatus.Rejected
            )
                return false;
            // Deferred can be overridden by anything except Unknown
            if (current == DeliveryStatus.Deferred)
                return incoming != DeliveryStatus.Unknown;
            // Delivered can be overridden by terminal statuses
            if (current == DeliveryStatus.Delivered)
                return incoming == DeliveryStatus.Bounced
                    || incoming == DeliveryStatus.SpamReport
                    || incoming == DeliveryStatus.Rejected;
            return true;
        }
    }
}
