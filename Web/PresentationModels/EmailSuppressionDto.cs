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
    /// Response for the admin clear endpoint. Not just the record: clearing a
    /// <see cref="SuppressionReason.ProviderUnsubscribe"/> suppression also reactivates the
    /// address at the email provider, and when that provider call fails the clear must not read
    /// as a silent success - <see cref="ProviderWarning"/> is how the admin learns the address
    /// may still be suppressed provider-side.
    /// </summary>
    public class ClearEmailSuppressionResponseDto
    {
        public EmailSuppressionDto Suppression { get; set; } = new();

        /// <summary>
        /// Human-readable warning when the provider-side reactivation did not fully succeed;
        /// null when it did, or when the record's reason involves no provider-side entry.
        /// </summary>
        public string? ProviderWarning { get; set; }
    }
}
