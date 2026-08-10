#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
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
    [Route("api/webhooks/postmark")]
    [ApiController]
    [AllowAnonymous]
    public class PostmarkWebhookController : ControllerBase
    {
        private readonly IPostmarkWebhookVerifier _verifier;
        private readonly IEmailDeliveryEventRepository _deliveryEventRepository;
        private readonly IEmailDeliveryActionService _deliveryActionService;
        private readonly ILogger<PostmarkWebhookController> _logger;
        private readonly bool _isDevelopment;

        public PostmarkWebhookController(
            IPostmarkWebhookVerifier verifier,
            IEmailDeliveryEventRepository deliveryEventRepository,
            IEmailDeliveryActionService deliveryActionService,
            IWebHostEnvironment env,
            ILogger<PostmarkWebhookController> logger
        )
        {
            _verifier = verifier;
            _deliveryEventRepository = deliveryEventRepository;
            _deliveryActionService = deliveryActionService;
            _logger = logger;
            _isDevelopment = env.IsDevelopment() || env.IsEnvironment("MockData");
        }

        [HttpPost]
        public async Task<IActionResult> HandleEvent()
        {
            string body;
            using (var reader = new StreamReader(Request.Body))
            {
                body = await reader.ReadToEndAsync();
            }

            // Verify webhook token — fail closed in production when not configured
            if (!_verifier.IsConfigured)
            {
                if (!_isDevelopment)
                {
                    _logger.LogWarning("Postmark webhook token not configured — rejecting request.");
                    return Forbid();
                }

                _logger.LogDebug("Postmark webhook token not configured — accepting in development mode.");
            }
            else
            {
                var token = Request.Headers["X-Postmark-Webhook-Token"].ToString();

                if (string.IsNullOrEmpty(token))
                {
                    _logger.LogWarning("Postmark webhook missing X-Postmark-Webhook-Token header.");
                    return Forbid();
                }

                if (!_verifier.Verify(token))
                {
                    _logger.LogWarning("Postmark webhook token verification failed.");
                    return Forbid();
                }
            }

            JsonElement evt;
            try
            {
                evt = JsonSerializer.Deserialize<JsonElement>(body);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse Postmark webhook body.");
                return BadRequest();
            }

            try
            {
                await ProcessEventAsync(evt, body);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing Postmark webhook event.");
                return StatusCode(500);
            }

            return Ok();
        }

        private async Task ProcessEventAsync(JsonElement evt, string rawBody)
        {
            if (evt.ValueKind != JsonValueKind.Object)
                return;

            var recordType = GetOptionalString(evt, "RecordType");
            if (string.IsNullOrEmpty(recordType))
                return;
            // SubscriptionChange is handled on its own path: job correlation is optional for it
            // (manual suppressions and reactivations carry no MessageID/metadata), and the
            // suppression write cannot wait on the per-job event sweep, which never sees
            // jobless events.
            if (recordType == "SubscriptionChange")
            {
                await ProcessSubscriptionChangeAsync(evt, rawBody);
                return;
            }

            // Extract recipient email — field name varies by event type
            string? email = recordType switch
            {
                "Delivery" => GetOptionalString(evt, "Recipient"),
                "Bounce" => GetOptionalString(evt, "Email"),
                "SpamComplaint" => GetOptionalString(evt, "Email"),
                _ => null,
            };

            if (string.IsNullOrEmpty(email))
            {
                _logger.LogDebug("Postmark {RecordType} event missing recipient email — skipping.", recordType);
                return;
            }

            email = email.Trim();

            // Extract correlation metadata
            string? jobIdStr = null;
            if (evt.TryGetProperty("Metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
            {
                jobIdStr = GetOptionalString(metadata, "cohad_job_id");
            }

            if (string.IsNullOrEmpty(jobIdStr))
            {
                _logger.LogDebug(
                    "Postmark {RecordType} event for {Email} missing cohad_job_id metadata — skipping.",
                    recordType,
                    email
                );
                return;
            }

            jobIdStr = jobIdStr.Trim();
            if (!Guid.TryParse(jobIdStr, out var jobId))
            {
                _logger.LogWarning("Postmark event has invalid cohad_job_id: {JobId}", jobIdStr);
                return;
            }

            var deliveryStatus = MapRecordTypeToDeliveryStatus(recordType, evt);

            // Extract MessageID for provider correlation
            string? messageId = GetOptionalString(evt, "MessageID");

            // Dedup key: use webhook event ID for bounces/complaints, MessageID-based key for deliveries
            string dedupKey = recordType switch
            {
                "Bounce" => ExtractBounceId(evt) ?? deliveryStatus.ToString(),
                "SpamComplaint" => ExtractComplaintId(evt) ?? deliveryStatus.ToString(),
                _ => !string.IsNullOrEmpty(messageId)
                    ? $"Delivery:{messageId}"
                    : deliveryStatus.ToString(),
            };

            var deliveryEvent = new EmailDeliveryEvent
            {
                Id = EmailDeliveryEvent.MakeId(jobId, email, dedupKey),
                JobId = jobId,
                Email = email,
                DeliveryStatus = deliveryStatus,
                ProviderEventType = recordType,
                ProviderEventId = ExtractEventId(recordType, evt),
                ProviderMessageId = messageId,
                Provider = "Postmark",
                ReceivedUtc = DateTime.UtcNow,
                ProviderPayloadJson = rawBody,
                ProviderDiagnostic = ExtractDiagnostic(recordType, evt),
            };

            await _deliveryEventRepository.AddAsync(deliveryEvent);
        }

        /// <summary>
        /// Handles a Postmark SubscriptionChange event: an address was added to or removed from
        /// a message stream's suppression list. The suppression mutation itself lives in
        /// <see cref="IEmailDeliveryActionService"/> (the provider-feedback writer); this method
        /// only extracts the payload and, when the change is a new suppression correlated to one
        /// of our jobs, stores a delivery event so the job's delivery-events view shows the
        /// unsubscribe. The event's <see cref="DeliveryStatus.Unknown"/> never overrides a
        /// truthful Delivered on the recipient row - the message WAS delivered; the unsubscribe
        /// happened afterwards, at the provider layer.
        /// </summary>
        private async Task ProcessSubscriptionChangeAsync(JsonElement evt, string rawBody)
        {
            var email = GetOptionalString(evt, "Recipient");
            if (string.IsNullOrWhiteSpace(email))
            {
                _logger.LogDebug("Postmark SubscriptionChange event missing Recipient - skipping.");
                return;
            }
            email = email.Trim();

            if (
                !evt.TryGetProperty("SuppressSending", out var suppressProp)
                || suppressProp.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            )
            {
                _logger.LogWarning(
                    "Postmark SubscriptionChange event for {Email} missing SuppressSending - skipping.",
                    email
                );
                return;
            }
            var suppressSending = suppressProp.GetBoolean();

            string? origin = GetOptionalString(evt, "Origin");
            string? postmarkReason = GetOptionalString(evt, "SuppressionReason");
            string? messageStream = GetOptionalString(evt, "MessageStream");
            string? messageId = GetOptionalString(evt, "MessageID");
            string? changedAt = GetOptionalString(evt, "ChangedAt");

            // Job correlation is optional: Postmark sends no MessageID (and empty Metadata) for
            // manual suppressions and reactivations. A missing or unparsable id is an absence,
            // not a rejection - unlike the delivery-event path above, which exists to correlate.
            Guid? jobId = null;
            if (
                evt.TryGetProperty("Metadata", out var metadata)
                && metadata.ValueKind == JsonValueKind.Object
            )
            {
                var jobIdStr = GetOptionalString(metadata, "cohad_job_id");
                if (Guid.TryParse(jobIdStr, out var parsed))
                {
                    jobId = parsed;
                }
                else if (!string.IsNullOrEmpty(jobIdStr))
                {
                    _logger.LogWarning("Postmark SubscriptionChange has invalid cohad_job_id: {JobId}", jobIdStr);
                }
            }

            // Deterministic per change so a webhook retry is idempotent. MessageID and ChangedAt
            // are both absent only on a malformed payload; hashing the raw body then keeps a
            // retried POST on the same key without falsely merging two distinct changes.
            var evidenceKey =
                $"postmark:subscription-change:{messageId ?? changedAt ?? ComputePayloadKey(rawBody)}";

            var action = await _deliveryActionService.ProcessSubscriptionChangeAsync(
                email,
                suppressSending,
                origin,
                postmarkReason,
                messageStream,
                jobId,
                evidenceKey
            );

            // Store a delivery event only when a suppression is in force AND the change names a
            // job: the per-job sweep reads events by job id, so a jobless event is unreachable,
            // and bounce/complaint-origin changes (SubscriptionChangeAction.None) already have
            // their own Bounce/SpamComplaint events. ActionProcessed is set because the action
            // was taken here, now - the sweep's gate would ignore an Unknown-status event
            // anyway, but the flag keeps that true if the gate ever widens.
            if (action != SubscriptionChangeAction.Suppressed || !jobId.HasValue)
                return;

            var deliveryEvent = new EmailDeliveryEvent
            {
                Id = EmailDeliveryEvent.MakeId(jobId.Value, email, evidenceKey),
                JobId = jobId.Value,
                Email = email,
                DeliveryStatus = DeliveryStatus.Unknown,
                ProviderEventType = "SubscriptionChange",
                ProviderMessageId = messageId,
                Provider = "Postmark",
                ReceivedUtc = DateTime.UtcNow,
                ProviderPayloadJson = rawBody,
                ProviderDiagnostic = EmailDeliveryActionService.BuildSubscriptionChangeDiagnostic(
                    origin,
                    postmarkReason,
                    messageStream
                ),
                ActionProcessed = true,
            };

            await _deliveryEventRepository.AddAsync(deliveryEvent);
        }

        /// <summary>
        /// Deterministic fallback identity for a payload carrying neither MessageID nor
        /// ChangedAt: a hash of the raw body, so a retried POST of the same bytes lands on the
        /// same evidence key and distinct payloads never share one.
        /// </summary>
        private static string ComputePayloadKey(string rawBody)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(rawBody));
            return Convert.ToHexStringLower(hash);
        }

        internal static DeliveryStatus MapRecordTypeToDeliveryStatus(string recordType, JsonElement evt)
        {
            return recordType switch
            {
                "Delivery" => DeliveryStatus.Delivered,
                "Bounce" => MapBounceType(evt),
                "SpamComplaint" => DeliveryStatus.SpamReport,
                _ => DeliveryStatus.Unknown,
            };
        }

        private static DeliveryStatus MapBounceType(JsonElement evt)
        {
            var bounceType = GetOptionalString(evt, "Type") ?? "";

            // Postmark bounce types: HardBounce, SoftBounce, Transient, etc.
            return bounceType switch
            {
                "Transient" => DeliveryStatus.Deferred,
                "SoftBounce" => DeliveryStatus.Deferred,
                _ => DeliveryStatus.Bounced,
            };
        }

        /// <summary>
        /// Reads an optional string property from a webhook payload. Postmark's schema says these
        /// fields are strings, but the payload is drift- and attacker-exposed: a non-string value
        /// (or a blank one) reads as absent rather than throwing the request into a 500-and-retry
        /// loop it can never escape.
        /// </summary>
        internal static string? GetOptionalString(JsonElement evt, string name)
        {
            if (!evt.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
                return null;
            var value = prop.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        /// <summary>
        /// Composes the provider's own explanation of a failure - "HardBounce: The server was
        /// unable to deliver..." - for the suppression record. Verbatim provider text, because for
        /// a bounce "why" IS the provider's text: it is what tells an admin whether the address is
        /// a typo or a mailbox that has closed. Null for deliveries, which need no explaining.
        /// </summary>
        internal static string? ExtractDiagnostic(string recordType, JsonElement evt)
        {
            if (recordType is not ("Bounce" or "SpamComplaint"))
                return null;

            var type = GetOptionalString(evt, "Type");
            var description = GetOptionalString(evt, "Description");
            if (string.IsNullOrWhiteSpace(description))
                description = GetOptionalString(evt, "Details");

            if (string.IsNullOrWhiteSpace(type))
                return string.IsNullOrWhiteSpace(description) ? null : description;

            return string.IsNullOrWhiteSpace(description) ? type : $"{type}: {description}";
        }

        private static string? ExtractBounceId(JsonElement evt)
        {
            if (evt.TryGetProperty("ID", out var idProp) && idProp.ValueKind == JsonValueKind.Number)
                return $"bounce-{idProp.GetInt64()}";
            return null;
        }

        private static string? ExtractComplaintId(JsonElement evt)
        {
            if (evt.TryGetProperty("ID", out var idProp) && idProp.ValueKind == JsonValueKind.Number)
                return $"complaint-{idProp.GetInt64()}";
            return null;
        }

        private static string? ExtractEventId(string recordType, JsonElement evt)
        {
            return recordType switch
            {
                "Bounce" => ExtractBounceId(evt),
                "SpamComplaint" => ExtractComplaintId(evt),
                "Delivery" => GetOptionalString(evt, "MessageID"),
                _ => null,
            };
        }
    }
}
