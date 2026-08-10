#nullable enable
using System;
using System.IO;
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

            if (!evt.TryGetProperty("RecordType", out var recordTypeProp))
                return;

            var recordType = recordTypeProp.GetString();
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
                "Delivery" => evt.TryGetProperty("Recipient", out var r) ? r.GetString() : null,
                "Bounce" => evt.TryGetProperty("Email", out var b) ? b.GetString() : null,
                "SpamComplaint" => evt.TryGetProperty("Email", out var s) ? s.GetString() : null,
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
            if (evt.TryGetProperty("Metadata", out var metadata))
            {
                if (metadata.TryGetProperty("cohad_job_id", out var jobIdProp))
                    jobIdStr = jobIdProp.GetString();
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
            string? messageId = null;
            if (evt.TryGetProperty("MessageID", out var msgIdProp))
                messageId = msgIdProp.GetString();

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
            var email = evt.TryGetProperty("Recipient", out var r) ? r.GetString() : null;
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

            string? origin = evt.TryGetProperty("Origin", out var o) ? o.GetString() : null;
            string? postmarkReason = evt.TryGetProperty("SuppressionReason", out var sr) ? sr.GetString() : null;
            string? messageStream = evt.TryGetProperty("MessageStream", out var ms) ? ms.GetString() : null;
            string? messageId =
                evt.TryGetProperty("MessageID", out var mid) && mid.ValueKind == JsonValueKind.String
                    ? mid.GetString()
                    : null;
            string? changedAt = evt.TryGetProperty("ChangedAt", out var ca) ? ca.GetString() : null;

            // Job correlation is optional: Postmark sends no MessageID (and empty Metadata) for
            // manual suppressions and reactivations. A missing or unparsable id is an absence,
            // not a rejection - unlike the delivery-event path above, which exists to correlate.
            Guid? jobId = null;
            if (
                evt.TryGetProperty("Metadata", out var metadata)
                && metadata.ValueKind == JsonValueKind.Object
                && metadata.TryGetProperty("cohad_job_id", out var jobIdProp)
            )
            {
                var jobIdStr = jobIdProp.GetString();
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
            // are both absent only on a malformed payload; a random key then keeps the write
            // applicable rather than falsely deduping two distinct changes into one.
            var evidenceKey =
                $"postmark:subscription-change:{messageId ?? changedAt ?? Guid.NewGuid().ToString("N")}";

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
                Id = EmailDeliveryEvent.MakeId(
                    jobId.Value,
                    email,
                    $"SubscriptionChange:{messageId ?? changedAt}"
                ),
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
            if (!evt.TryGetProperty("Type", out var typeProp))
                return DeliveryStatus.Bounced;

            var bounceType = typeProp.GetString() ?? "";

            // Postmark bounce types: HardBounce, SoftBounce, Transient, etc.
            return bounceType switch
            {
                "Transient" => DeliveryStatus.Deferred,
                "SoftBounce" => DeliveryStatus.Deferred,
                _ => DeliveryStatus.Bounced,
            };
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

            var type = evt.TryGetProperty("Type", out var typeProp) ? typeProp.GetString() : null;
            var description = evt.TryGetProperty("Description", out var descProp) ? descProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(description))
                description = evt.TryGetProperty("Details", out var detailsProp) ? detailsProp.GetString() : null;

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
                "Delivery" => evt.TryGetProperty("MessageID", out var m) ? m.GetString() : null,
                _ => null,
            };
        }
    }
}
