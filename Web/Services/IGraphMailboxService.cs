using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Web.Models;

namespace Web.Services
{
    /// <summary>
    /// Manages inbox forwarding rules on O365 shared mailboxes via the Microsoft Graph API.
    /// </summary>
    public interface IGraphMailboxService
    {
        /// <summary>
        /// Creates or updates the <c>forwardTo</c> inbox rule on the committee's shared mailbox
        /// so that incoming mail is forwarded to members where <see cref="CommitteeMember.ReceivesForwardedEmail"/> is true.
        /// The <paramref name="residents"/> lookup is used to resolve email addresses from member <see cref="CommitteeMember.ResidentId"/> references.
        /// Updates <see cref="Committee.GraphMessageRuleId"/>, <see cref="Committee.LastSyncedUtc"/>,
        /// <see cref="Committee.LastSyncStatus"/>, and <see cref="Committee.LastSyncError"/> on the returned object.
        /// </summary>
        Task<Committee> SyncForwardingRuleAsync(Committee committee, IReadOnlyDictionary<Guid, Resident> residents);
    }
}
