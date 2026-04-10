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

        public HeldMessageStatus Status { get; set; }

        public string ReviewedByUserId { get; set; }

        public DateTime? ReviewedUtc { get; set; }

        [JsonIgnore]
        public string ETag { get; set; }
    }
}
