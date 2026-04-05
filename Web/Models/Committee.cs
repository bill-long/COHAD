using System;
using System.Collections.Generic;

namespace Web.Models
{
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

        public string GraphMessageRuleId { get; set; }

        public DateTime? LastSyncedUtc { get; set; }

        public string LastSyncStatus { get; set; }

        public string LastSyncError { get; set; }
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
