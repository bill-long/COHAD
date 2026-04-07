#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Web.Configuration;
using Web.Models;
using Web.Services;
using Web.Services.Repositories;

namespace Web.Controllers
{
    [Route("api/webhooks/ses")]
    [ApiController]
    [AllowAnonymous]
    public class SesWebhookController : ControllerBase
    {
        private readonly IEmailJobRepository _emailJobRepository;
        private readonly IEmailDeliveryActionService _deliveryActionService;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<SesWebhookController> _logger;
        private readonly bool _isDevelopment;
        private readonly HashSet<string> _allowedTopicArns;

        /// <summary>
        /// Validates that the SNS signing cert URL matches the documented SNS format:
        /// https://sns.{region}.amazonaws.com/SimpleNotificationService-*.pem
        /// </summary>
        private static bool IsValidSnsCertUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return false;
            if (uri.Scheme != "https")
                return false;
            // Host must be sns.{region}.amazonaws.com
            var host = uri.Host;
            if (!host.StartsWith("sns.", StringComparison.OrdinalIgnoreCase)
                || !host.EndsWith(".amazonaws.com", StringComparison.OrdinalIgnoreCase))
                return false;
            // Path must contain SimpleNotificationService and end with .pem
            var path = uri.AbsolutePath;
            if (!path.Contains("SimpleNotificationService", StringComparison.Ordinal)
                || !path.EndsWith(".pem", StringComparison.OrdinalIgnoreCase))
                return false;
            // No query string or fragment
            if (!string.IsNullOrEmpty(uri.Query) && uri.Query != "?")
                return false;
            if (!string.IsNullOrEmpty(uri.Fragment))
                return false;
            return true;
        }

        /// <summary>
        /// Maximum age of an SNS message before it is rejected (replay prevention).
        /// </summary>
        private static readonly TimeSpan MaxMessageAge = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Cache of SNS signing certificates keyed by URL. Avoids downloading the
        /// certificate on every webhook request. Entries live for the process lifetime
        /// which is acceptable — SNS rotates certs infrequently and new URLs get new entries.
        /// </summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, RSAParameters> _certCache = new();

        public SesWebhookController(
            IEmailJobRepository emailJobRepository,
            IEmailDeliveryActionService deliveryActionService,
            IHttpClientFactory httpClientFactory,
            IOptions<SesOptions> sesOptions,
            IWebHostEnvironment env,
            ILogger<SesWebhookController> logger
        )
        {
            _emailJobRepository = emailJobRepository;
            _deliveryActionService = deliveryActionService;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _isDevelopment = env.IsDevelopment() || env.IsEnvironment("MockData");
            var opts = sesOptions.Value;
            _allowedTopicArns = new HashSet<string>(
                (opts.AllowedTopicArns ?? Enumerable.Empty<string>())
                    .Select(a => a.Trim())
                    .Where(a => a.Length > 0),
                StringComparer.Ordinal
            );
        }

        [HttpPost]
        public async Task<IActionResult> HandleNotification()
        {
            string body;
            using (var reader = new StreamReader(Request.Body))
            {
                body = await reader.ReadToEndAsync();
            }

            JsonElement message;
            try
            {
                message = JsonSerializer.Deserialize<JsonElement>(body);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse SNS notification body.");
                return BadRequest();
            }

            // Determine SNS message type
            if (!message.TryGetProperty("Type", out var typeProp))
            {
                _logger.LogWarning("SNS message missing Type property.");
                return BadRequest();
            }

            var messageType = typeProp.GetString();

            // Verify SNS signature
            if (!await VerifySnsSignatureAsync(message))
            {
                if (!_isDevelopment)
                {
                    _logger.LogWarning("SNS signature verification failed — rejecting.");
                    return Forbid();
                }
                _logger.LogDebug("SNS signature verification failed — accepting in development mode.");
            }

            // Validate TopicArn allowlist (prevents rogue SNS topics from triggering opt-outs)
            if (_allowedTopicArns.Count > 0)
            {
                var topicArn = message.TryGetProperty("TopicArn", out var arnProp) ? arnProp.GetString() : null;
                if (string.IsNullOrEmpty(topicArn) || !_allowedTopicArns.Contains(topicArn))
                {
                    _logger.LogWarning("SNS message from unexpected TopicArn: {TopicArn} — rejecting.", topicArn);
                    return Forbid();
                }
            }

            // Reject messages without a valid timestamp to prevent bypassing replay-age checks
            if (!message.TryGetProperty("Timestamp", out var tsProp))
            {
                if (!_isDevelopment)
                {
                    _logger.LogWarning("SNS message missing Timestamp property — rejecting.");
                    return BadRequest();
                }
                _logger.LogDebug("SNS message missing Timestamp — accepting in development mode.");
            }
            else if (!DateTime.TryParse(tsProp.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var msgTime))
            {
                if (!_isDevelopment)
                {
                    _logger.LogWarning("SNS message has invalid Timestamp '{Timestamp}' — rejecting.", tsProp.GetString());
                    return BadRequest();
                }
                _logger.LogDebug("SNS message has invalid Timestamp — accepting in development mode.");
            }
            else
            {
                var nowUtc = DateTime.UtcNow;
                var futureClockSkewTolerance = TimeSpan.FromMinutes(5);
                if (msgTime - nowUtc > futureClockSkewTolerance)
                {
                    _logger.LogWarning("SNS message timestamp too far in the future ({Timestamp}) — rejecting.", tsProp.GetString());
                    return BadRequest();
                }
                if (nowUtc - msgTime > MaxMessageAge)
                {
                    _logger.LogWarning("SNS message timestamp too old ({Timestamp}) — rejecting.", tsProp.GetString());
                    return BadRequest();
                }
            }

            switch (messageType)
            {
                case "SubscriptionConfirmation":
                    return await HandleSubscriptionConfirmationAsync(message);

                case "Notification":
                    return await HandleSesNotificationAsync(message);

                default:
                    _logger.LogDebug("Ignoring SNS message type: {Type}", messageType);
                    return Ok();
            }
        }

        private async Task<IActionResult> HandleSubscriptionConfirmationAsync(JsonElement message)
        {
            if (!message.TryGetProperty("SubscribeURL", out var urlProp))
            {
                _logger.LogWarning("SNS SubscriptionConfirmation missing SubscribeURL.");
                return BadRequest();
            }

            var subscribeUrl = urlProp.GetString();
            if (string.IsNullOrEmpty(subscribeUrl))
                return BadRequest();

            // Validate the SubscribeURL is an amazonaws.com domain
            if (
                !Uri.TryCreate(subscribeUrl, UriKind.Absolute, out var uri)
                || uri.Scheme != "https"
                || !uri.Host.EndsWith(".amazonaws.com", StringComparison.OrdinalIgnoreCase)
            )
            {
                _logger.LogWarning("SNS SubscribeURL has unexpected host: {Url}", subscribeUrl);
                return BadRequest();
            }

            try
            {
                var client = _httpClientFactory.CreateClient();
                using var response = await client.GetAsync(subscribeUrl, HttpContext.RequestAborted);
                response.EnsureSuccessStatusCode();
                _logger.LogInformation("Confirmed SNS subscription: {Url}", subscribeUrl);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to confirm SNS subscription.");
            }

            return Ok();
        }

        private async Task<IActionResult> HandleSesNotificationAsync(JsonElement snsMessage)
        {
            if (!snsMessage.TryGetProperty("Message", out var messageProp))
                return Ok();

            var sesMessageStr = messageProp.GetString();
            if (string.IsNullOrEmpty(sesMessageStr))
                return Ok();

            JsonElement sesEvent;
            try
            {
                sesEvent = JsonSerializer.Deserialize<JsonElement>(sesMessageStr);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse SES notification message.");
                return Ok(); // Don't make SNS retry for bad messages
            }

            if (!sesEvent.TryGetProperty("notificationType", out var notifTypeProp))
            {
                // SES v2 events use "eventType" instead of "notificationType"
                if (!sesEvent.TryGetProperty("eventType", out notifTypeProp))
                    return Ok();
            }

            var notificationType = notifTypeProp.GetString();
            if (string.IsNullOrEmpty(notificationType))
                return Ok();

            // Extract job ID and email from SES message tags
            var (jobIdStr, email) = ExtractCorrelationTags(sesEvent);
            if (string.IsNullOrEmpty(jobIdStr) || string.IsNullOrEmpty(email))
            {
                _logger.LogDebug(
                    "SES event {Type} missing cohad_job_id or cohad_email tags — skipping.",
                    notificationType
                );
                return Ok();
            }

            if (!Guid.TryParse(jobIdStr, out var jobId))
            {
                _logger.LogWarning("SES event has invalid cohad_job_id: {JobId}", jobIdStr);
                return Ok();
            }

            var deliveryStatus = MapSesEventToDeliveryStatus(sesEvent, notificationType);
            if (deliveryStatus == DeliveryStatus.Unknown)
                return Ok();

            // Extract SES message ID
            string? sesMessageId = null;
            if (
                sesEvent.TryGetProperty("mail", out var mailProp)
                && mailProp.TryGetProperty("messageId", out var msgIdProp)
            )
            {
                sesMessageId = msgIdProp.GetString();
            }

            // Update the job recipient with concurrency retry
            const int maxRetries = 3;
            for (var attempt = 1; attempt <= maxRetries; attempt++)
            {
                var job = await _emailJobRepository.GetByIdAsync(jobId);
                if (job == null)
                {
                    _logger.LogDebug("SES event for unknown job {JobId} — skipping.", jobId);
                    return Ok();
                }

                var recipient = job.Recipients?.FirstOrDefault(r =>
                    string.Equals(r.Email, email, StringComparison.OrdinalIgnoreCase)
                );
                if (recipient == null)
                {
                    _logger.LogDebug("SES event for job {JobId} — recipient {Email} not found.", jobId, email);
                    return Ok();
                }

                try
                {
                    var shouldUpdateStatus = DeliveryStatusHelper.ShouldUpdate(
                        recipient.DeliveryStatus,
                        deliveryStatus
                    );
                    var shouldSetProviderMessageId =
                        !string.IsNullOrEmpty(sesMessageId) && string.IsNullOrEmpty(recipient.ProviderMessageId);

                    if (shouldUpdateStatus)
                    {
                        recipient.DeliveryStatus = deliveryStatus;
                        recipient.DeliveryStatusUpdatedUtc = DateTime.UtcNow;
                    }

                    if (shouldSetProviderMessageId)
                    {
                        recipient.ProviderMessageId = sesMessageId;
                    }

                    if (shouldUpdateStatus || shouldSetProviderMessageId)
                    {
                        await _emailJobRepository.UpdateAsync(job);
                    }

                    if (deliveryStatus == DeliveryStatus.Bounced || deliveryStatus == DeliveryStatus.SpamReport)
                    {
                        await _deliveryActionService.ProcessDeliveryEventAsync(email, deliveryStatus, job.Category);
                    }

                    return Ok();
                }
                catch (EmailJobConcurrencyException) when (attempt < maxRetries)
                {
                    _logger.LogWarning(
                        "Concurrency conflict updating delivery status for job {JobId}, recipient {Email}. Retry {Attempt}/{MaxRetries}.",
                        jobId,
                        email,
                        attempt,
                        maxRetries
                    );
                }
            }

            return Ok();
        }

        private static (string? jobId, string? email) ExtractCorrelationTags(JsonElement sesEvent)
        {
            string? jobId = null;
            string? email = null;

            // SES message tags are in mail.tags (set via EmailTags on the SendEmail request)
            if (sesEvent.TryGetProperty("mail", out var mail) && mail.TryGetProperty("tags", out var tags))
            {
                // SES tags are { "tagName": ["value1"] } format
                if (
                    tags.TryGetProperty("cohad_job_id", out var jobIdTag)
                    && jobIdTag.ValueKind == JsonValueKind.Array
                    && jobIdTag.GetArrayLength() > 0
                )
                {
                    jobId = jobIdTag[0].GetString()?.Trim();
                }

                if (
                    tags.TryGetProperty("cohad_email", out var emailTag)
                    && emailTag.ValueKind == JsonValueKind.Array
                    && emailTag.GetArrayLength() > 0
                )
                {
                    email = emailTag[0].GetString()?.Trim();
                }
            }

            return (jobId, email);
        }

        internal static DeliveryStatus MapSesEventToDeliveryStatus(JsonElement sesEvent, string notificationType)
        {
            switch (notificationType.ToLowerInvariant())
            {
                case "delivery":
                    return DeliveryStatus.Delivered;

                case "bounce":
                    // Distinguish permanent vs transient bounces
                    if (
                        sesEvent.TryGetProperty("bounce", out var bounce)
                        && bounce.TryGetProperty("bounceType", out var bounceType)
                    )
                    {
                        var type = bounceType.GetString()?.ToLowerInvariant();
                        return type == "transient" ? DeliveryStatus.Deferred : DeliveryStatus.Bounced;
                    }
                    return DeliveryStatus.Bounced;

                case "complaint":
                    return DeliveryStatus.SpamReport;

                case "reject":
                    return DeliveryStatus.Rejected;

                default:
                    return DeliveryStatus.Unknown;
            }
        }

        /// <summary>
        /// Verifies the SNS message signature by downloading the signing certificate
        /// from the URL specified in the message and validating the signature.
        /// </summary>
        private async Task<bool> VerifySnsSignatureAsync(JsonElement message)
        {
            try
            {
                if (!message.TryGetProperty("SigningCertURL", out var certUrlProp))
                {
                    // Also check SigningCertUrl (case variation)
                    if (!message.TryGetProperty("SigningCertUrl", out certUrlProp))
                        return false;
                }

                var certUrl = certUrlProp.GetString();
                if (string.IsNullOrEmpty(certUrl))
                    return false;

                // Validate the certificate URL matches the documented SNS cert format
                if (!IsValidSnsCertUrl(certUrl))
                {
                    _logger.LogWarning("SNS signing cert URL has unexpected format: {Url}", certUrl);
                    return false;
                }

                if (!message.TryGetProperty("Signature", out var sigProp))
                    return false;
                var signatureBase64 = sigProp.GetString();
                if (string.IsNullOrEmpty(signatureBase64))
                    return false;

                // Build the string to sign (per SNS specification)
                var stringToSign = BuildSnsStringToSign(message);
                if (stringToSign == null)
                    return false;

                // Get or download the signing certificate's RSA public key (cached by URL)
                RSAParameters rsaParams;
                if (!_certCache.TryGetValue(certUrl, out rsaParams))
                {
                    var client = _httpClientFactory.CreateClient();
                    var certPem = await client.GetStringAsync(certUrl, HttpContext.RequestAborted);
                    var cert = X509Certificate2.CreateFromPem(certPem);
                    using var rsaKey = cert.GetRSAPublicKey();
                    if (rsaKey == null)
                        return false;
                    rsaParams = rsaKey.ExportParameters(false);
                    _certCache.TryAdd(certUrl, rsaParams);
                }

                using var rsa = RSA.Create(rsaParams);

                // Determine hash algorithm from SignatureVersion (v1=SHA1, v2=SHA256)
                var sigVersion = message.TryGetProperty("SignatureVersion", out var sigVerProp)
                    ? sigVerProp.GetString() : "1";
                HashAlgorithmName hashAlg;
                switch (sigVersion)
                {
                    case "1":
                        hashAlg = HashAlgorithmName.SHA1;
                        break;
                    case "2":
                        hashAlg = HashAlgorithmName.SHA256;
                        break;
                    default:
                        _logger.LogWarning("Unsupported SNS SignatureVersion: {Version}", sigVersion);
                        return false;
                }

                var signatureBytes = Convert.FromBase64String(signatureBase64);
                var messageBytes = Encoding.UTF8.GetBytes(stringToSign);

                return rsa.VerifyData(messageBytes, signatureBytes, hashAlg, RSASignaturePadding.Pkcs1);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SNS signature verification error.");
                return false;
            }
        }

        /// <summary>
        /// Builds the canonical string-to-sign for SNS message signature verification.
        /// See: https://docs.aws.amazon.com/sns/latest/dg/sns-verify-signature-of-message.html
        /// </summary>
        private static string? BuildSnsStringToSign(JsonElement message)
        {
            if (!message.TryGetProperty("Type", out var typeProp))
                return null;

            var type = typeProp.GetString();
            var sb = new StringBuilder();
            sb.Append("Message\n");
            sb.Append(GetStringProp(message, "Message") + "\n");
            sb.Append("MessageId\n");
            sb.Append(GetStringProp(message, "MessageId") + "\n");

            if (type == "SubscriptionConfirmation" || type == "UnsubscribeConfirmation")
            {
                sb.Append("SubscribeURL\n");
                sb.Append(GetStringProp(message, "SubscribeURL") + "\n");
            }

            if (message.TryGetProperty("Subject", out _))
            {
                sb.Append("Subject\n");
                sb.Append(GetStringProp(message, "Subject") + "\n");
            }

            sb.Append("Timestamp\n");
            sb.Append(GetStringProp(message, "Timestamp") + "\n");

            if (type == "SubscriptionConfirmation" || type == "UnsubscribeConfirmation")
            {
                sb.Append("Token\n");
                sb.Append(GetStringProp(message, "Token") + "\n");
            }

            sb.Append("TopicArn\n");
            sb.Append(GetStringProp(message, "TopicArn") + "\n");
            sb.Append("Type\n");
            sb.Append(type + "\n");

            return sb.ToString();
        }

        private static string GetStringProp(JsonElement element, string property)
        {
            return element.TryGetProperty(property, out var prop) ? prop.GetString() ?? "" : "";
        }
    }
}
