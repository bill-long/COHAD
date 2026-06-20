#nullable enable
using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Web.Models
{
    /// <summary>
    /// Per-recipient throttle state for notification escalation digests. Records when a recipient last
    /// received an escalation digest so the sweeper can enforce a minimum interval between digests and
    /// avoid emailing the same person repeatedly as items age past the grace period at different times.
    /// </summary>
    public class NotificationDigestState
    {
        /// <summary>The recipient's email address, normalized to lower-case (the document's natural key).</summary>
        public string RecipientEmail { get; set; } = string.Empty;

        /// <summary>When this recipient was last sent an escalation digest.</summary>
        public DateTime LastDigestUtc { get; set; }

        [JsonIgnore]
        public string? ETag { get; set; }

        /// <summary>
        /// Stable id for a recipient's digest-state document, derived from the normalized email so a
        /// read/write always addresses the same record. Hashed (rather than embedding the raw email)
        /// to keep the id free of characters Cosmos disallows in ids (e.g. '/', '\', '?', '#').
        /// </summary>
        public static string DeterministicId(string recipientEmail)
        {
            var normalized = (recipientEmail ?? string.Empty).Trim().ToLowerInvariant();
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
            return Convert.ToHexString(bytes);
        }
    }
}
