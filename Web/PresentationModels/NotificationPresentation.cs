#nullable enable
using System;
using Web.Models;

namespace Web.PresentationModels
{
    /// <summary>
    /// Client-facing view of a unified in-app <see cref="Notification"/>. Excludes server-only state
    /// (ETag, escalation tracking) and exposes the target so the client can deep-link to the
    /// underlying moderation UI (vendor flag, held message) that already resolves it.
    /// </summary>
    public class NotificationPresentation
    {
        public Guid Id { get; set; }

        public NotificationType Type { get; set; }

        public string AudienceKey { get; set; } = string.Empty;

        public string TargetType { get; set; } = string.Empty;

        public string TargetId { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public string Summary { get; set; } = string.Empty;

        public DateTime CreatedUtc { get; set; }

        public static NotificationPresentation FromStorageModel(Notification n)
        {
            return new NotificationPresentation
            {
                Id = n.Id,
                Type = n.Type,
                AudienceKey = n.AudienceKey,
                TargetType = n.TargetType,
                TargetId = n.TargetId,
                Title = n.Title,
                Summary = n.Summary,
                CreatedUtc = n.CreatedUtc,
            };
        }
    }
}
