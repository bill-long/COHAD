#nullable enable
using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Web.Models
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum NotificationType
    {
        Registration = 0,
        VendorFlag = 1,
        HeldMessage = 2,
    }

    /// <summary>
    /// Canonical <see cref="Notification.AudienceKey"/> values. Centralized so trigger sites
    /// (raise) and consumers (the controller's audience resolution) can't drift on the string format.
    /// </summary>
    public static class NotificationAudience
    {
        /// <summary>Audience for notifications any Administrator may act on (registrations, vendor flags).</summary>
        public const string Administrators = "role:Administrator";

        /// <summary>Prefix for a single committee's audience key.</summary>
        public const string CommitteePrefix = "committee:";

        /// <summary>Audience for a single committee's moderators (held committee emails).</summary>
        public static string Committee(string committeeId) => $"{CommitteePrefix}{committeeId}";

        /// <summary>
        /// Extracts the committee id from a committee audience key, or null if the key is not a
        /// committee audience. Keeps consumers (e.g. escalation recipient resolution) from re-deriving
        /// the <see cref="CommitteePrefix"/> format and drifting from <see cref="Committee"/>.
        /// </summary>
        public static string? TryGetCommitteeId(string audienceKey)
        {
            if (string.IsNullOrEmpty(audienceKey) || !audienceKey.StartsWith(CommitteePrefix, StringComparison.Ordinal))
                return null;
            var id = audienceKey.Substring(CommitteePrefix.Length);
            return string.IsNullOrEmpty(id) ? null : id;
        }
    }

    /// <summary>Canonical <see cref="Notification.TargetType"/> values for the underlying domain records.</summary>
    public static class NotificationTargetType
    {
        public const string User = "user";
        public const string VendorFlag = "vendorFlag";
        public const string HeldMessage = "heldMessage";
    }

    /// <summary>
    /// A unified in-app notification about an event that someone may need to act on.
    /// The notification references an underlying domain record (a new <see cref="User"/>, a
    /// <see cref="VendorFlag"/>, a <see cref="HeldMessage"/>) via <see cref="TargetType"/> +
    /// <see cref="TargetId"/> rather than duplicating its content — that record remains the source
    /// of truth. Two state flags are tracked independently:
    /// <list type="bullet">
    /// <item><see cref="ResolvedUtc"/> — a human acted (dismissed/approved/rejected/acknowledged).</item>
    /// <item><see cref="EscalatedUtc"/> — the notification was already included in an escalation
    /// email digest, so the sweeper must not email about it again.</item>
    /// </list>
    /// </summary>
    public class Notification
    {
        public Guid Id { get; set; }

        public NotificationType Type { get; set; }

        /// <summary>
        /// The set of people who can act on this notification, e.g. <c>"role:Administrator"</c> or
        /// <c>"committee:{committeeId}"</c>. Used both to group SignalR pushes and to resolve email
        /// recipients during escalation.
        /// </summary>
        public string AudienceKey { get; set; } = string.Empty;

        /// <summary>Kind of the underlying domain record, e.g. <c>"user"</c>, <c>"vendorFlag"</c>, <c>"heldMessage"</c>.</summary>
        public string TargetType { get; set; } = string.Empty;

        /// <summary>Identifier of the underlying domain record within <see cref="TargetType"/>.</summary>
        public string TargetId { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public string Summary { get; set; } = string.Empty;

        public DateTime CreatedUtc { get; set; }

        /// <summary>Set when a human acts on the underlying item. Null while the item is still pending.</summary>
        public DateTime? ResolvedUtc { get; set; }

        public string? ResolvedBy { get; set; }

        /// <summary>Set when this notification has been included in an escalation email digest.</summary>
        public DateTime? EscalatedUtc { get; set; }

        /// <summary>The <see cref="EmailJob"/> that escalated this notification, if any.</summary>
        public Guid? EscalationJobId { get; set; }

        [JsonIgnore]
        public string? ETag { get; set; }

        /// <summary>
        /// True for notification types whose only resolution is an explicit acknowledge/dismiss because
        /// they have no underlying moderation action (registrations). Types backed by a domain action
        /// (vendor flags, held emails) must be resolved by that action — acknowledging them directly
        /// would hide still-pending work and defeat escalation, so callers must reject them.
        /// </summary>
        public static bool IsAcknowledgeable(NotificationType type) => type == NotificationType.Registration;

        /// <summary>
        /// Produces a stable id for a notification target so that a duplicate raise (e.g. two
        /// concurrent registrations of the same event) maps to the same document and a racing
        /// create fails with 409 rather than producing a duplicate notification. The id is keyed on
        /// the target alone — matching the service's idempotency check and the 409-recovery re-read —
        /// since <paramref name="targetType"/> + <paramref name="targetId"/> already uniquely identify
        /// the underlying record (the notification's <see cref="Type"/> is derived from its target).
        /// </summary>
        public static Guid DeterministicId(string targetType, string targetId)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{targetType}\n{targetId}"));
            var guidBytes = new byte[16];
            Array.Copy(bytes, guidBytes, 16);
            return new Guid(guidBytes);
        }
    }
}
