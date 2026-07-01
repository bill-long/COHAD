#nullable enable
using System;
using Web.Models;

namespace Web.PresentationModels
{
    /// <summary>
    /// Client-facing view of a held committee email awaiting moderation, enriched with its committee's
    /// display name so the Approvals inbox can render a flat cross-committee queue without a second
    /// lookup per row. Excludes server-only state (ETag, InternetMessageId) the moderator never needs.
    /// </summary>
    public class PendingHeldMessagePresentation
    {
        public Guid Id { get; set; }

        public string CommitteeId { get; set; } = string.Empty;

        public string CommitteeName { get; set; } = string.Empty;

        // Nullable to preserve "no sender/subject" — the client renders those as "unknown sender",
        // which a coerced empty string would silently defeat.
        public string? SenderEmail { get; set; }

        public string? SenderName { get; set; }

        public string? Subject { get; set; }

        public DateTime ReceivedUtc { get; set; }

        public DateTime HeldUtc { get; set; }

        public static PendingHeldMessagePresentation FromStorageModel(HeldMessage m, string committeeName)
        {
            return new PendingHeldMessagePresentation
            {
                Id = m.Id,
                CommitteeId = m.CommitteeId ?? string.Empty,
                CommitteeName = committeeName,
                SenderEmail = m.SenderEmail,
                SenderName = m.SenderName,
                Subject = m.Subject,
                ReceivedUtc = m.ReceivedUtc,
                HeldUtc = m.HeldUtc,
            };
        }
    }
}
