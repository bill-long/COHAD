#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
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
            string? committeeDisplayName,
            string? senderEmail,
            string? senderName
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

        /// <summary>
        /// Chooses the recipient list for a committee forward, preferring a deliverable address for
        /// each member: the first non-blank address not in <paramref name="suppressedAddresses"/>.
        /// A member all of whose addresses are suppressed is excluded (their mail is stopped), and a
        /// member with no addresses at all is skipped silently, as before the suppression list
        /// existed. Recipients are deduplicated by address (case-insensitive, first member wins),
        /// matching the pre-hoist behavior at both call sites.
        /// <para>
        /// This is the single selection implementation behind both forwarding paths. Alerting a
        /// committee's moderators when one of their forwarding members has no deliverable address is
        /// a separate concern with its own resolution lifecycle - see the deferred follow-up in
        /// docs/email-suppression-and-unsubscribe.md, Part 3.
        /// </para>
        /// </summary>
        /// <param name="suppressedAddresses">
        /// Addresses in <see cref="EmailSuppression.NormalizeAddress"/> form; candidate addresses
        /// are normalized through the same rule before the comparison.
        /// </param>
        public static List<EmailJobRecipient> SelectForwardRecipients(
            IReadOnlyList<CommitteeMember> forwardingMembers,
            IReadOnlyDictionary<Guid, Resident> residentsById,
            IReadOnlySet<string> suppressedAddresses
        )
        {
            ArgumentNullException.ThrowIfNull(forwardingMembers);
            ArgumentNullException.ThrowIfNull(residentsById);
            ArgumentNullException.ThrowIfNull(suppressedAddresses);

            var recipients = new List<EmailJobRecipient>();

            foreach (var member in forwardingMembers)
            {
                var resident = residentsById.GetValueOrDefault(member.ResidentId);
                var address = FirstDeliverableAddress(resident, suppressedAddresses);
                if (address == null)
                    continue; // No address, or every address suppressed - the member gets nothing.

                recipients.Add(new EmailJobRecipient
                {
                    Email = address,
                    HomeId = resident!.HomeId,
                    Status = EmailJobRecipientStatus.Pending,
                });
            }

            return recipients
                .GroupBy(r => r.Email, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }

        /// <summary>
        /// The address a committee forward would use for one member: the first non-blank address not
        /// in <paramref name="suppressedAddresses"/> (<see cref="EmailSuppression.NormalizeAddress"/>
        /// form), or null when the resident has no addresses or every one is suppressed. The single
        /// per-member rule, shared by <see cref="SelectForwardRecipients"/> and the admin
        /// forwarding-status preview so the preview cannot claim a member (or address) the send would
        /// not actually use.
        /// </summary>
        public static string? FirstDeliverableAddress(Resident? resident, IReadOnlySet<string> suppressedAddresses)
        {
            ArgumentNullException.ThrowIfNull(suppressedAddresses);

            return (resident?.EmailAddresses ?? Enumerable.Empty<EmailAddress>())
                .Select(e => e?.Address)
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Cast<string>()
                .FirstOrDefault(a => !suppressedAddresses.Contains(EmailSuppression.NormalizeAddress(a)));
        }
    }
}
