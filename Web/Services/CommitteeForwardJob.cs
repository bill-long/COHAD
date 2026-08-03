using System;
using Web.Models;

namespace Web.Services
{
    /// <summary>
    /// Metadata shared by the two committee-forwarding paths - <see cref="CommitteeMailPoller"/>
    /// (automatic forwarding) and admin approval of a held message in <c>CommitteeController</c>.
    /// Both produce the same kind of job, so the rules for describing it live here rather than
    /// being duplicated at each call site.
    /// </summary>
    internal static class CommitteeForwardJob
    {
        /// <summary>
        /// Stamps the job with the original author and the audience description.
        /// <para>
        /// A forwarded message is sent <em>as</em> the committee mailbox, so the job's
        /// <see cref="EmailJob.FromEmail"/> is the committee - the human who wrote it is recorded
        /// separately in <see cref="EmailJob.OriginalSenderEmail"/>, and as the Reply-To so replies
        /// reach them instead of the mailbox. A blank sender address (some system-generated mail has
        /// none) leaves all sender fields null rather than inventing an unreplyable address.
        /// </para>
        /// </summary>
        public static void ApplyOriginator(
            EmailJob job,
            string committeeDisplayName,
            string senderEmail,
            string senderName
        )
        {
            ArgumentNullException.ThrowIfNull(job);

            var hasSender = !string.IsNullOrWhiteSpace(senderEmail);

            job.OriginalSenderEmail = hasSender ? senderEmail : null;
            job.OriginalSenderDisplay = hasSender ? senderName : null;
            job.ReplyToEmail = hasSender ? senderEmail : null;
            job.ReplyToDisplay = hasSender ? senderName : null;
            job.ToDisplay = EmailAudience.ForCommitteeForward(committeeDisplayName);
        }
    }
}
