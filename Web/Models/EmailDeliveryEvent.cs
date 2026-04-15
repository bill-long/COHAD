#nullable enable
using System;
using System.Security.Cryptography;
using System.Text;

namespace Web.Models
{
    /// <summary>
    /// A single delivery event from an email provider webhook (SendGrid or SES).
    /// Each event is stored as its own Cosmos document — zero contention.
    /// The email job processor reads and applies these to the job at its convenience.
    /// </summary>
    public class EmailDeliveryEvent
    {
        /// <summary>Unique ID for deduplication (e.g. "{jobId}|{email}|{eventType}").</summary>
        public string Id { get; set; } = "";

        /// <summary>The email job this event belongs to.</summary>
        public Guid JobId { get; set; }

        /// <summary>Recipient email address.</summary>
        public string Email { get; set; } = "";

        /// <summary>Mapped delivery status from the provider event.</summary>
        public DeliveryStatus DeliveryStatus { get; set; }

        /// <summary>Provider-specific message ID (e.g. sg_message_id, SES messageId).</summary>
        public string? ProviderMessageId { get; set; }

        /// <summary>Which provider sent the event (e.g. "SendGrid", "Ses").</summary>
        public string? Provider { get; set; }

        /// <summary>When this event was received by our webhook endpoint.</summary>
        public DateTime ReceivedUtc { get; set; }

        /// <summary>Whether the delivery action (auto opt-out) has been processed.</summary>
        public bool ActionProcessed { get; set; }

        /// <summary>
        /// Generates a deterministic, fixed-length ID for deduplication.
        /// Multiple events for the same job+email+status are collapsed to one document.
        /// Uses SHA256 hash to keep below the 255-char Cosmos DB id limit regardless
        /// of email address length.
        /// </summary>
        public static string MakeId(Guid jobId, string email, DeliveryStatus status)
        {
            var input = $"{jobId:D}|{email.ToLowerInvariant()}|{status}";
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexStringLower(hash); // 64 chars
        }
    }
}
