#nullable enable
using System;
using Web.Models;

namespace Web.PresentationModels
{
    /// <summary>
    /// A suppression record for the Administrator surfaces. Carries the full address, not a
    /// redaction: the page's purpose is identifying which address is suppressed, and it sits
    /// behind the Administrator policy - the audit log is where redaction applies.
    /// </summary>
    public class EmailSuppressionDto
    {
        public string Id { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public SuppressionReason Reason { get; set; }
        public int ConsecutiveFailureCount { get; set; }
        public DateTime FirstSeenUtc { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public Guid? CausingJobId { get; set; }
        public DateTime SuppressedUtc { get; set; }
        public string SuppressedBy { get; set; } = string.Empty;
        public string? ProviderDiagnostic { get; set; }
        public DateTime? ClearedUtc { get; set; }
        public string? ClearedBy { get; set; }
        public bool IsActive { get; set; }

        public static EmailSuppressionDto FromModel(EmailSuppression s)
        {
            return new EmailSuppressionDto
            {
                Id = s.Id,
                Email = s.Email,
                Reason = s.Reason,
                ConsecutiveFailureCount = s.ConsecutiveFailureCount,
                FirstSeenUtc = s.FirstSeenUtc,
                LastSeenUtc = s.LastSeenUtc,
                CausingJobId = s.CausingJobId,
                SuppressedUtc = s.SuppressedUtc,
                SuppressedBy = s.SuppressedBy,
                ProviderDiagnostic = s.ProviderDiagnostic,
                ClearedUtc = s.ClearedUtc,
                ClearedBy = s.ClearedBy,
                IsActive = s.IsActive,
            };
        }
    }

    /// <summary>Body for the admin create endpoint.</summary>
    public class CreateEmailSuppressionDto
    {
        public string Email { get; set; } = string.Empty;
    }

    /// <summary>
    /// Body for the admin clear endpoint: which suppression episode the admin is acting on.
    /// <see cref="SuppressedUtc"/> is the row's displayed value - it identifies the episode
    /// (every re-suppression resets it), so a record re-suppressed between page load and click
    /// is refused with 409 instead of being lifted on stale information. Null (an older or
    /// non-UI caller) skips the episode guard.
    /// </summary>
    public class ClearEmailSuppressionRequestDto
    {
        public DateTime? SuppressedUtc { get; set; }
    }
}
