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
        /// The author of a forwarded message. Null unless the caller is an Administrator - see
        /// <see cref="FromJob(EmailJob, bool)"/>.
        /// </summary>
        public string OriginalSenderEmail { get; set; }

        /// <summary>
        /// Display name for <see cref="OriginalSenderEmail"/>, withheld from non-administrators alongside it.
        /// </summary>
        public string OriginalSenderDisplay { get; set; }

        /// <summary>
        /// True when this job has an author but the caller was not allowed to see it. Lets the client
        /// tell "you may not see who wrote this" apart from "this forward genuinely had no sender
        /// address" (auto-replies and mailer daemons), which would otherwise read identically.
        /// Carries no part of the identity it is hiding.
        /// </summary>
        public bool OriginalSenderWithheld { get; set; }

        public string Subject { get; set; }

        public DateTime CreatedUtc { get; set; }

        public DateTime? StartedUtc { get; set; }

        public DateTime? CompletedUtc { get; set; }

        public string CreatedByDisplayName { get; set; }

        public int TotalRecipients { get; set; }

        public int SentCount { get; set; }

        public int FailedCount { get; set; }

        public string LastError { get; set; }

        /// <summary>
        /// Maps a job for the caller.
        /// <para>
        /// <paramref name="includeOriginalSender"/> must be true only for Administrators. A forwarded
        /// message's author is a third party who wrote to one committee, while the job endpoints are
        /// gated by the committee-agnostic "EmailSender" policy - so every committee role can read
        /// every job. Those callers get no part of the author's identity; the client tells them the
        /// sender is not shown (see <see cref="OriginalSenderWithheld"/>) rather than naming anyone.
        /// </para>
        /// </summary>
        public static EmailJobSummary FromJob(EmailJob job, bool includeOriginalSender = false)
        {
            var dto = new EmailJobSummary();
            Populate(dto, job, includeOriginalSender);
            return dto;
        }

        /// <summary>
        /// Copies the shared fields onto <paramref name="dto"/>. Lives here so
        /// <see cref="EmailJobDetail"/> cannot drift from the list view it extends.
        /// </summary>
        protected static void Populate(EmailJobSummary dto, EmailJob job, bool includeOriginalSender)
        {
            var resolvedSender = job.ResolveOriginalSender();
            var originalSender = includeOriginalSender ? resolvedSender : null;

            dto.OriginalSenderWithheld = !includeOriginalSender && resolvedSender != null;
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
            dto.LastError = job.LastError;
        }
    }
}
