#nullable enable
using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Web.Services;

namespace Web.Models
{
    /// <summary>
    /// Why an address is suppressed. Serialized by name so the stored document reads as evidence
    /// rather than as a magic number.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum SuppressionReason
    {
        /// <summary>The provider reported a hard bounce for the address.</summary>
        HardBounce = 0,

        /// <summary>The recipient reported the mail as spam via their provider.</summary>
        SpamComplaint = 1,

        /// <summary>The recipient asked for the mail to stop (RFC 8058 one-click).</summary>
        ResidentRequest = 2,

        /// <summary>An administrator suppressed the address by hand.</summary>
        AdminAction = 3,
    }

    /// <summary>
    /// A do-not-mail record for one address. All-mail and category-free: it answers "does this
    /// address receive anything", never "which lists is it on" - per-category choice stays in the
    /// five opt-in booleans, which a suppression leaves untouched underneath it. The two are
    /// evaluated independently at the single enforcement point in <c>EmailJobProcessor</c>.
    /// <para>
    /// Keyed on the normalized address, one document per address, forever: repeat evidence updates
    /// the counters, a clear stamps <see cref="ClearedUtc"/> rather than deleting, and a
    /// re-suppression after a clear reuses the document. Episode history beyond the current state
    /// lives in the audit log. See docs/email-suppression-and-unsubscribe.md, Part 3.
    /// </para>
    /// <para>
    /// The record has to explain itself, because it goes on screen: an admin looking at a
    /// suppressed address learns when, why, and by what from these fields alone, without reading
    /// logs.
    /// </para>
    /// </summary>
    public class EmailSuppression
    {
        /// <summary>
        /// The Cosmos document id: <see cref="MakeId"/> over the normalized address, so a lookup is
        /// a point read. A hash rather than the <c>{Type}|{key}</c> prefix convention because email
        /// local parts may legally contain <c>/ \ ? #</c>, none of which are allowed in a Cosmos id.
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>The suppressed address, stored in <see cref="NormalizeAddress"/> form.</summary>
        public string Email { get; set; } = string.Empty;

        /// <summary>Why the address is (or was last) suppressed.</summary>
        public SuppressionReason Reason { get; set; }

        /// <summary>
        /// How many pieces of hard evidence (bounce, complaint, unsubscribe, admin action) have
        /// accumulated on this record. Exists so a future soft-bounce N-in-window rule has a field
        /// to build on; nothing acts on the number today.
        /// </summary>
        public int ConsecutiveFailureCount { get; set; }

        /// <summary>When the first evidence arrived. Survives clears and re-suppressions.</summary>
        public DateTime FirstSeenUtc { get; set; }

        /// <summary>When the most recent evidence arrived.</summary>
        public DateTime LastSeenUtc { get; set; }

        /// <summary>The email job whose delivery produced the evidence, when one is known.</summary>
        public Guid? CausingJobId { get; set; }

        /// <summary>When the current suppression began. Reset by a re-suppression after a clear.</summary>
        public DateTime SuppressedUtc { get; set; }

        /// <summary>
        /// Who or what caused the current suppression: <see cref="SystemDeliveryEvent"/>, the
        /// credential type for a link-driven unsubscribe, or the admin's user id.
        /// </summary>
        public string SuppressedBy { get; set; } = string.Empty;

        /// <summary>
        /// The provider's own diagnostic for a bounce or complaint - Postmark's type and
        /// description. Kept verbatim because "why" for a hard bounce is the provider's text:
        /// paraphrasing it into a reason code throws away the part that tells an admin whether the
        /// address is a typo or a mailbox that has closed.
        /// </summary>
        public string? ProviderDiagnostic { get; set; }

        /// <summary>
        /// The dedup key of the most recent piece of evidence applied to this record - the
        /// delivery event's deterministic id. What makes <c>RecordAsync</c> idempotent per
        /// evidence: the delivery-event path is at-least-once (the event is marked processed only
        /// after the suppression write), and without this key a replayed event increments
        /// <see cref="ConsecutiveFailureCount"/> into a lie. Null for evidence with no natural key
        /// (one-click, admin action), which is fine - those paths are not replayed mechanically.
        /// </summary>
        public string? LastEvidenceKey { get; set; }

        /// <summary>When the suppression was cleared, or null while it is in force.</summary>
        public DateTime? ClearedUtc { get; set; }

        /// <summary>Who cleared it: an admin's user id, or <c>resident:{credentialType}</c>.</summary>
        public string? ClearedBy { get; set; }

        [JsonIgnore]
        public string? ETag { get; set; }

        /// <summary>Whether the suppression is currently in force.</summary>
        [JsonIgnore]
        public bool IsActive => ClearedUtc == null;

        /// <summary>The <see cref="SuppressedBy"/> value for the provider-feedback writer.</summary>
        public const string SystemDeliveryEvent = "system:delivery-event";

        /// <summary>
        /// The one normalization rule for suppression keys. Every consumer - the writers, the
        /// enforcement map, the forwarding compare, the preferences lookup - must go through this
        /// (or compare with <c>OrdinalIgnoreCase</c> on trimmed values, which is equivalent);
        /// a second rule is how the same address ends up both suppressed and mailed.
        /// </summary>
        public static string NormalizeAddress(string email)
        {
            return UserEmailHelpers.NormalizeEmail(email);
        }

        /// <summary>
        /// Derives the document id from the address: SHA-256 hex of the normalized form. Stable,
        /// case- and whitespace-insensitive, and safe for any characters an address can contain.
        /// </summary>
        public static string MakeId(string email)
        {
            var normalized = NormalizeAddress(email);
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }
}
