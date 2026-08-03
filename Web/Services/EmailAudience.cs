using System;

namespace Web.Services
{
    /// <summary>
    /// Every description written to <see cref="EmailJob.ToDisplay"/>. One place, because the phrasing
    /// carries a rule: the distinguishing word comes first, so a description stays recognizable when a
    /// narrow table cell truncates it. A second producer elsewhere would quietly break that.
    /// </summary>
    internal static class EmailAudience
    {
        /// <summary>
        /// A committee forward. Says "forwarding members" rather than "members" because both
        /// forwarding paths select recipients with <c>Where(m =&gt; m.ReceivesForwardedEmail)</c>, so
        /// the wider word would describe people who were never addressed. The count beside it can
        /// still be lower - members without an email are skipped and shared addresses are deduped -
        /// so this names who was addressed, not how many messages went out.
        /// </summary>
        public static string ForCommitteeForward(string committeeDisplayName) =>
            string.IsNullOrWhiteSpace(committeeDisplayName)
                ? "Committee forwarding members"
                : $"{committeeDisplayName} forwarding members";

        /// <summary>
        /// A committee-wide send, whose recipients are the addresses opted in to that committee's mail.
        /// <paramref name="committeeLabel"/> is the committee's human name ("Board", "Garden Club").
        /// </summary>
        public static string ForCommitteeSend(string committeeLabel) =>
            string.IsNullOrWhiteSpace(committeeLabel) ? "Opt-in residents" : $"{committeeLabel} opt-in residents";

        /// <summary>A test send, which goes only to the sender's own addresses.</summary>
        public const string TestRecipients = "Test recipients";

        /// <summary>
        /// A notification escalation digest. Names the audience rather than the recipient: a digest goes
        /// to exactly one moderator, and echoing the address would put a personal address in the job list.
        /// </summary>
        public const string NotificationModerator = "Notification moderator";
    }
}
