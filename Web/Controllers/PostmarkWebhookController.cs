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
        private readonly ILogger<PostmarkWebhookController> _logger;
        private readonly bool _isDevelopment;

        public PostmarkWebhookController(
            IPostmarkWebhookVerifier verifier,
            IEmailDeliveryEventRepository deliveryEventRepository,
            IWebHostEnvironment env,
            ILogger<PostmarkWebhookController> logger
        )
        {
            _verifier = verifier;
            _deliveryEventRepository = deliveryEventRepository;
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
