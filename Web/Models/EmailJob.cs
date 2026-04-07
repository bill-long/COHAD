using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Web.Models
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum EmailJobStatus
    {
        Queued = 0,
        InProgress = 1,
        Completed = 2,
        PartiallyCompleted = 3,
        Failed = 4,
        Cancelled = 5,
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum EmailJobRecipientStatus
    {
        Pending = 0,
        Sent = 1,
        Failed = 2,
    }

    public class EmailJob
    {
        public Guid Id { get; set; }

        public EmailJobStatus Status { get; set; }

        /// <summary>
        /// Unsubscribe category key (e.g. "board", "welcome").
        /// </summary>
        public string Category { get; set; }

        public string FromEmail { get; set; }

        public string FromDisplay { get; set; }

        public string Subject { get; set; }

        /// <summary>
        /// Blob path where the HTML body is stored (e.g. "email-jobs/{id}.html").
        /// The HTML body is stored separately to avoid exceeding Cosmos DB's 2 MB document limit
        /// when the Quill editor embeds base64 images.
        /// </summary>
        public string ContentBlobPath { get; set; }

        public DateTime CreatedUtc { get; set; }

        public DateTime? StartedUtc { get; set; }

        public DateTime? CompletedUtc { get; set; }

        /// <summary>
        /// Updated when progress is made sending recipients. Used to detect stalled jobs.
        /// </summary>
        public DateTime? LastProgressUtc { get; set; }

        public string CreatedByUserId { get; set; }

        public string CreatedByDisplayName { get; set; }

        /// <summary>
        /// Max number of send attempts per recipient for this job.
        /// Stored on the job for auditability and stable behavior across restarts.
        /// </summary>
        public int MaxRecipientAttempts { get; set; }

        public int TotalRecipients { get; set; }

        public int SentCount { get; set; }

        public int FailedCount { get; set; }

        /// <summary>
        /// Last error message (from a fatal/connection-level failure, not per-recipient errors).
        /// </summary>
        public string LastError { get; set; }

        /// <summary>
        /// When true, all recipients are placed in a single message's To: field
        /// instead of sending individual messages. Useful for admin notifications
        /// where Reply All is desirable.
        /// </summary>
        public bool GroupRecipients { get; set; }

        public List<EmailJobRecipient> Recipients { get; set; } = new();

        /// <summary>
        /// Cosmos DB ETag for optimistic concurrency. Not serialized to/from the document body;
        /// populated from the Cosmos response headers.
        /// </summary>
        [JsonIgnore]
        public string ETag { get; set; }
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum DeliveryStatus
    {
        Unknown = 0,
        Delivered = 1,
        Bounced = 2,
        Deferred = 3,
        SpamReport = 4,
        Rejected = 5,
    }

    public class EmailJobRecipient
    {
        public string Email { get; set; }

        public Guid HomeId { get; set; }

        public EmailJobRecipientStatus Status { get; set; }

        public int AttemptCount { get; set; }

        public DateTime? LastAttemptUtc { get; set; }

        public string Error { get; set; }

        public DateTime? SentUtc { get; set; }

        /// <summary>
        /// Post-delivery status reported by the email provider's webhook (e.g. delivered, bounced).
        /// Separate from <see cref="Status"/> which tracks relay acceptance.
        /// </summary>
        public DeliveryStatus DeliveryStatus { get; set; }

        public DateTime? DeliveryStatusUpdatedUtc { get; set; }

        /// <summary>
        /// Provider-specific message ID for debugging and correlation.
        /// For SendGrid, populated from the <c>sg_message_id</c> field in webhook events.
        /// </summary>
        public string? ProviderMessageId { get; set; }

        /// <summary>
        /// Which email provider sent this message (e.g. "SendGrid", "Ses").
        /// </summary>
        public string? Provider { get; set; }
    }
}
