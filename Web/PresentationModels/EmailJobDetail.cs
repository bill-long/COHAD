using System;
using System.Collections.Generic;
using System.Linq;
using Web.Models;

namespace Web.PresentationModels
{
    /// <summary>
    /// Full DTO for the single-job status endpoint - the list fields plus per-recipient details.
    /// Extends <see cref="EmailJobSummary"/> so the two views cannot report a job differently.
    /// </summary>
    public class EmailJobDetail : EmailJobSummary
    {
        public List<EmailJobRecipientDetail> Recipients { get; set; } = new();

        public static new EmailJobDetail FromJob(EmailJob job)
        {
            var dto = new EmailJobDetail
            {
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

            Populate(dto, job);
            return dto;
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
