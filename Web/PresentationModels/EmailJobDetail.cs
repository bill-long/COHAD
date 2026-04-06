using System;
using System.Collections.Generic;
using System.Linq;
using Web.Models;

namespace Web.PresentationModels
{
    /// <summary>
    /// Full DTO for the single-job status endpoint — includes per-recipient details.
    /// </summary>
    public class EmailJobDetail
    {
        public Guid Id { get; set; }

        public EmailJobStatus Status { get; set; }

        public string Category { get; set; }

        public string FromDisplay { get; set; }

        public string Subject { get; set; }

        public DateTime CreatedUtc { get; set; }

        public DateTime? StartedUtc { get; set; }

        public DateTime? CompletedUtc { get; set; }

        public string CreatedByDisplayName { get; set; }

        public int TotalRecipients { get; set; }

        public int SentCount { get; set; }

        public int FailedCount { get; set; }

        public string LastError { get; set; }

        public List<EmailJobRecipientDetail> Recipients { get; set; } = new();

        public static EmailJobDetail FromJob(EmailJob job)
        {
            return new EmailJobDetail
            {
                Id = job.Id,
                Status = job.Status,
                Category = job.Category,
                FromDisplay = job.FromDisplay,
                Subject = job.Subject,
                CreatedUtc = job.CreatedUtc,
                StartedUtc = job.StartedUtc,
                CompletedUtc = job.CompletedUtc,
                CreatedByDisplayName = job.CreatedByDisplayName,
                TotalRecipients = job.TotalRecipients,
                SentCount = job.SentCount,
                FailedCount = job.FailedCount,
                LastError = job.LastError,
                Recipients =
                    job.Recipients?.Select(r => new EmailJobRecipientDetail
                        {
                            Email = r.Email,
                            Status = r.Status,
                            Error = r.Error,
                            SentUtc = r.SentUtc,
                            DeliveryStatus = r.DeliveryStatus,
                            DeliveryStatusUpdatedUtc = r.DeliveryStatusUpdatedUtc,
                            Provider = r.Provider,
                        })
                        .ToList()
                    ?? new(),
            };
        }
    }

    public class EmailJobRecipientDetail
    {
        public string Email { get; set; }

        public EmailJobRecipientStatus Status { get; set; }

        public string Error { get; set; }

        public DateTime? SentUtc { get; set; }

        public DeliveryStatus DeliveryStatus { get; set; }

        public DateTime? DeliveryStatusUpdatedUtc { get; set; }

        public string? Provider { get; set; }
    }
}
