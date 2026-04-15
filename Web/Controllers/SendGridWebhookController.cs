#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
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
        private readonly IEmailDeliveryEventRepository _deliveryEventRepository;
        private readonly ILogger<SendGridWebhookController> _logger;
        private readonly bool _isDevelopment;

        private static readonly HashSet<string> TrackedEventTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "delivered",
            "bounce",
            "dropped",
            "spamreport",
            "deferred",
        };

        /// <summary>Maximum age of a webhook timestamp before it's rejected (anti-replay).</summary>
        private static readonly TimeSpan TimestampFreshnessWindow = TimeSpan.FromMinutes(5);

        public SendGridWebhookController(
            ISendGridWebhookVerifier verifier,
            IEmailDeliveryEventRepository deliveryEventRepository,
            IWebHostEnvironment env,
            ILogger<SendGridWebhookController> logger
        )
        {
            _verifier = verifier;
            _deliveryEventRepository = deliveryEventRepository;
            _logger = logger;
            _isDevelopment = env.IsDevelopment() || env.IsEnvironment("MockData");
        }

        [HttpPost]
        public async Task<IActionResult> HandleEvents()
        {
            string body;
            using (var reader = new StreamReader(Request.Body))
            {
                body = await reader.ReadToEndAsync();
            }

            // Verify signature — fail closed in production when key is not configured
            if (!_verifier.IsConfigured)
            {
                if (!_isDevelopment)
                {
                    _logger.LogWarning("SendGrid webhook verification key not configured — rejecting request.");
                    return Forbid();
                }

                _logger.LogDebug("SendGrid webhook verification not configured — accepting in development mode.");
            }
            else
            {
                var signature = Request.Headers["X-Twilio-Email-Event-Webhook-Signature"].FirstOrDefault();
                var timestamp = Request.Headers["X-Twilio-Email-Event-Webhook-Timestamp"].FirstOrDefault();

                if (string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(timestamp))
                {
                    _logger.LogWarning("SendGrid webhook missing required signature or timestamp header.");
                    return Forbid();
                }

                if (!_verifier.Verify(body, signature, timestamp))
                {
                    _logger.LogWarning("SendGrid webhook signature verification failed.");
                    return Forbid();
                }

                // Reject stale or non-parsable timestamps to prevent replay attacks
                if (!long.TryParse(timestamp, out var epochSeconds))
                {
                    _logger.LogWarning(
                        "SendGrid webhook timestamp {Timestamp} is not a valid integer — rejecting.",
                        timestamp
                    );
                    return Forbid();
                }

                if (
                    Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - epochSeconds)
                    > (long)TimestampFreshnessWindow.TotalSeconds
                )
                {
                    _logger.LogWarning(
                        "SendGrid webhook timestamp {Timestamp} outside freshness window — rejecting.",
                        timestamp
                    );
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

            var hasFailure = false;
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
                    hasFailure = true;
                }
            }

            // Return 500 so SendGrid retries the entire batch on transient failures
            if (hasFailure)
                return StatusCode(500);

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

            jobIdStr = jobIdStr.Trim();
            email = email.Trim();

            if (string.IsNullOrEmpty(jobIdStr) || string.IsNullOrEmpty(email))
            {
                _logger.LogDebug(
                    "SendGrid event {EventType} has whitespace-only cohad_job_id or cohad_email — skipping.",
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

            // Write the delivery event to its own container — no contention possible.
            var deliveryEvent = new EmailDeliveryEvent
            {
                Id = EmailDeliveryEvent.MakeId(jobId, email, deliveryStatus),
                JobId = jobId,
                Email = email,
                DeliveryStatus = deliveryStatus,
                ProviderMessageId = sgMessageId,
                Provider = "SendGrid",
                ReceivedUtc = DateTime.UtcNow,
            };

            await _deliveryEventRepository.AddAsync(deliveryEvent);
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
    }
}
