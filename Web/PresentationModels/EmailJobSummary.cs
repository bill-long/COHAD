using System;
using Web.Models;

namespace Web.PresentationModels
{
    /// <summary>
    /// Lightweight DTO for the email job list endpoint - excludes per-recipient details.
    /// </summary>
    public class EmailJobSummary
    {
        public Guid Id { get; set; }

        public EmailJobStatus Status { get; set; }

        public string Category { get; set; }

        public string FromEmail { get; set; }

        public string FromDisplay { get; set; }

        public string ToDisplay { get; set; }

        /// <summary>
        /// The author of a forwarded message. Null when the message originated in COHAD, and also when
        /// a forward's incoming message carried no sender address at all - an auto-reply or a mailer
        /// daemon - in which case the client falls back to the mailbox it was sent as.
        /// </summary>
        public string OriginalSenderEmail { get; set; }

        /// <summary>
        /// Display name for <see cref="OriginalSenderEmail"/>.
        /// </summary>
        public string OriginalSenderDisplay { get; set; }

        public string Subject { get; set; }

        public DateTime CreatedUtc { get; set; }

        public DateTime? StartedUtc { get; set; }

        public DateTime? CompletedUtc { get; set; }

        public string CreatedByDisplayName { get; set; }

        public int TotalRecipients { get; set; }

        public int SentCount { get; set; }

        public int FailedCount { get; set; }

        /// <summary>
        /// Recipients skipped by the suppression list. Without this the list view shows
        /// "Completed, sent 3 of 5" with no explanation, which reads as data loss.
        /// </summary>
        public int SuppressedCount { get; set; }

        public string LastError { get; set; }

        public static EmailJobSummary FromJob(EmailJob job)
        {
            var dto = new EmailJobSummary();
            Populate(dto, job);
            return dto;
        }

        /// <summary>
        /// Copies the shared fields onto <paramref name="dto"/>. Lives here so
        /// <see cref="EmailJobDetail"/> cannot drift from the list view it extends.
        /// </summary>
        protected static void Populate(EmailJobSummary dto, EmailJob job)
        {
            var originalSender = job.ResolveOriginalSender();

            dto.Id = job.Id;
            dto.Status = job.Status;
            dto.Category = job.Category;
            dto.FromEmail = job.FromEmail;
            dto.FromDisplay = job.FromDisplay;
            dto.ToDisplay = job.ToDisplay;
            dto.OriginalSenderEmail = originalSender?.Email;
            dto.OriginalSenderDisplay = originalSender?.Display;
            dto.Subject = job.Subject;
            dto.CreatedUtc = job.CreatedUtc;
            dto.StartedUtc = job.StartedUtc;
            dto.CompletedUtc = job.CompletedUtc;
            dto.CreatedByDisplayName = job.CreatedByDisplayName;
            dto.TotalRecipients = job.TotalRecipients;
            dto.SentCount = job.SentCount;
            dto.FailedCount = job.FailedCount;
            dto.SuppressedCount = job.SuppressedCount;
            dto.LastError = job.LastError;
        }
    }
}
