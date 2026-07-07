using System;
using System.Text.Json.Serialization;

namespace Web.Models
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum HeldMessageStatus
    {
        Held = 0,
        Approved = 1,
        Rejected = 2,
        Expired = 3,
    }

    /// <summary>
    /// A message from an unknown sender that was held for admin review
    /// instead of being automatically forwarded to committee members.
    /// </summary>
    public class HeldMessage
    {
        public Guid Id { get; set; }

        public string CommitteeId { get; set; }

        public string CommitteeEmail { get; set; }

        /// <summary>
        /// The RFC 2822 Internet Message-ID of the original message in the shared mailbox.
        /// Stable across folder moves (unlike the Graph message <c>id</c>).
        /// Used to correlate with the mailbox message for approve/reject actions.
        /// </summary>
        public string InternetMessageId { get; set; }

        public string SenderEmail { get; set; }

        public string SenderName { get; set; }

        public string Subject { get; set; }

        public DateTime ReceivedUtc { get; set; }

        public DateTime HeldUtc { get; set; }

        /// <summary>
        /// Set once the committee's moderators have been notified about this held message. Null while
        /// the message is still inside the antispam quarantine window — held (and kept out of forwarding)
        /// but deliberately not surfaced to people yet, so automatic antispam has time to act on it first.
        /// </summary>
        public DateTime? NotifiedUtc { get; set; }

        public HeldMessageStatus Status { get; set; }

        /// <summary>
        /// The LLM spam classifier's verdict for this message, computed once at hold time from the sender,
        /// subject, and body. Retained as an audit trail: a message the antispam sweep auto-rejects as spam
        /// keeps its verdict, confidence, and reason so a moderator can later review why it was filtered.
        /// <see cref="SpamVerdict.Unknown"/> when classification is disabled or failed.
        /// </summary>
        public SpamVerdict SpamVerdict { get; set; }

        /// <summary>The classifier's confidence in <see cref="SpamVerdict"/>. See <see cref="SpamConfidence"/>.</summary>
        public SpamConfidence SpamConfidence { get; set; }

        /// <summary>The classifier's one-line justification for its verdict, kept for auditing auto-rejections.</summary>
        public string SpamReason { get; set; }

        public string ReviewedByUserId { get; set; }

        public DateTime? ReviewedUtc { get; set; }

        [JsonIgnore]
        public string ETag { get; set; }
    }
}
