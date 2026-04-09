using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Web.Models
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ForwardingSenderFilter
    {
        DirectoryOnly = 0,
        All = 1,
    }

    public class Committee
    {
        public string Id { get; set; }

        public string CommitteeEmail { get; set; }

        public string DisplayName { get; set; }

        public string Description { get; set; }

        public int DisplayOrder { get; set; }

        public List<CommitteeMember> Members { get; set; } = new List<CommitteeMember>();

        /// <summary>
        /// The user role that grants management access to this committee.
        /// Administrators can always manage any committee regardless of this field.
        /// </summary>
        public User.Role? ManagementRole { get; set; }

        // ── Legacy forwarding rule fields (kept for migration) ──────────
        public string GraphMessageRuleId { get; set; }

        public DateTime? LastSyncedUtc { get; set; }

        public string LastSyncStatus { get; set; }

        public string LastSyncError { get; set; }

        // ── Polling mail gateway fields ─────────────────────────────────

        /// <summary>
        /// When true, the <see cref="CommitteeMailPoller"/> reads this committee's
        /// shared mailbox and forwards messages to members with <see cref="CommitteeMember.ReceivesForwardedEmail"/>.
        /// </summary>
        public bool ForwardingEnabled { get; set; }

        /// <summary>
        /// Controls whether only directory-listed senders are forwarded (<see cref="ForwardingSenderFilter.DirectoryOnly"/>)
        /// or all senders (<see cref="ForwardingSenderFilter.All"/>).
        /// Unknown senders are held for admin review when set to <see cref="ForwardingSenderFilter.DirectoryOnly"/>.
        /// </summary>
        public ForwardingSenderFilter ForwardingSenderFilter { get; set; }

        public DateTime? LastPollUtc { get; set; }

        public string LastPollStatus { get; set; }

        public string LastPollError { get; set; }
    }

    public class CommitteeMember
    {
        public Guid Id { get; set; }

        /// <summary>
        /// Reference to the <see cref="Resident"/> entity. Display name and email are
        /// resolved from the linked resident at read time — never duplicated here.
        /// </summary>
        public Guid ResidentId { get; set; }

        /// <summary>
        /// Optional position title (e.g. "President", "Chair"). Null when the member has no title.
        /// </summary>
        public string Title { get; set; }

        /// <summary>Optional biography text.</summary>
        public string Bio { get; set; }

        public string PhotoBlobPath { get; set; }

        public string PhotoContentType { get; set; }

        /// <summary>
        /// When true, this member receives forwarded email from the committee's O365 shared mailbox.
        /// </summary>
        public bool ReceivesForwardedEmail { get; set; }

        /// <summary>
        /// Vertical offset percentage (0–100) for positioning the photo within a circular crop.
        /// Default 50 centers the image; lower values shift up (show more of the top).
        /// </summary>
        public int PhotoOffsetY { get; set; } = 50;

        public int DisplayOrder { get; set; }
    }
}
