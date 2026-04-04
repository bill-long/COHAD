using System;
using System.Collections.Generic;

namespace Web.UpdateModels
{
    public class CommitteeUpdateRequest
    {
        public string Description { get; set; }

        public int? DisplayOrder { get; set; }

        public List<CommitteeMemberUpdate> Members { get; set; }
    }

    public class CommitteeMemberUpdate
    {
        /// <summary>
        /// Member ID. Clients may supply a new UUID for new members.
        /// If null or <see cref="Guid.Empty"/>, the server assigns one.
        /// </summary>
        public Guid? Id { get; set; }

        public string DisplayName { get; set; }

        public string Title { get; set; }

        public string Bio { get; set; }

        public string Email { get; set; }

        public bool ReceivesForwardedEmail { get; set; }

        public int PhotoOffsetY { get; set; } = 50;

        public int DisplayOrder { get; set; }
    }
}
