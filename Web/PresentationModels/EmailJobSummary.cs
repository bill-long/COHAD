using System;
using Web.Models;

namespace Web.PresentationModels
{
    /// <summary>
    /// Lightweight DTO for the email job list endpoint — excludes per-recipient details.
    /// </summary>
    public class EmailJobSummary
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

        public static EmailJobSummary FromJob(EmailJob job)
        {
            return new EmailJobSummary
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
            };
        }
    }
}
