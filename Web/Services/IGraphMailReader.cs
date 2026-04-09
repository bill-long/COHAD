#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graph.Models;

namespace Web.Services
{
    /// <summary>
    /// Reads and manages messages in O365 shared mailboxes via the Microsoft Graph API.
    /// Used by <see cref="CommitteeMailPoller"/> to implement the polling mail gateway.
    /// Requires <c>Mail.ReadWrite</c> application permission only (no <c>Mail.Send</c>).
    /// </summary>
    public interface IGraphMailReader
    {
        /// <summary>
        /// Returns all messages currently in the Inbox of the specified mailbox.
        /// Messages are returned with basic properties (Id, Subject, From, ReceivedDateTime, Body).
        /// </summary>
        Task<List<Message>> GetInboxMessagesAsync(string mailbox, CancellationToken ct = default);

        /// <summary>
        /// Returns a single message with its attachments expanded.
        /// </summary>
        Task<Message?> GetMessageWithAttachmentsAsync(string mailbox, string messageId, CancellationToken ct = default);

        /// <summary>
        /// Moves a message to the specified folder by ID.
        /// </summary>
        Task MoveMessageAsync(string mailbox, string messageId, string destinationFolderId, CancellationToken ct = default);

        /// <summary>
        /// Returns the folder ID for the given display name, creating it if it does not exist.
        /// </summary>
        Task<string> GetOrCreateFolderAsync(string mailbox, string folderName, CancellationToken ct = default);

        /// <summary>
        /// Deletes an inbox message rule from the specified mailbox.
        /// Used during migration from the legacy inbox-rule forwarding to the new polling gateway.
        /// </summary>
        Task DeleteMessageRuleAsync(string mailbox, string ruleId, CancellationToken ct = default);
    }
}
