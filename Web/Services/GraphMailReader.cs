#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.Messages.Item.Move;

namespace Web.Services
{
    /// <summary>
    /// Reads and manages messages in O365 shared mailboxes via Microsoft Graph API.
    /// Uses client-credentials (application permission) flow with <c>Mail.ReadWrite</c>.
    /// </summary>
    public sealed class GraphMailReader : IGraphMailReader
    {
        private readonly GraphServiceClient _graphClient;
        private readonly ILogger<GraphMailReader> _logger;

        public GraphMailReader(IConfiguration config, ILogger<GraphMailReader> logger)
        {
            _logger = logger;

            var tenantId = config["Graph:TenantId"];
            var clientId = config["Graph:ClientId"];
            var clientSecret = config["Graph:ClientSecret"];

            if (string.IsNullOrWhiteSpace(tenantId)
                || string.IsNullOrWhiteSpace(clientId)
                || string.IsNullOrWhiteSpace(clientSecret))
            {
                throw new InvalidOperationException(
                    "Graph API credentials are not configured. "
                    + "Ensure Graph:TenantId, Graph:ClientId, and Graph:ClientSecret are set."
                );
            }

            var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
            _graphClient = new GraphServiceClient(credential, new[] { "https://graph.microsoft.com/.default" });
        }

        public async Task<List<Message>> GetInboxMessagesAsync(string mailbox, CancellationToken ct = default)
        {
            var messages = new List<Message>();

            var page = await _graphClient
                .Users[mailbox]
                .MailFolders["inbox"]
                .Messages.GetAsync(
                    config =>
                    {
                        config.QueryParameters.Select = new[]
                        {
                            "id",
                            "internetMessageId",
                            "subject",
                            "from",
                            "receivedDateTime",
                            "body",
                            "hasAttachments",
                        };
                        config.QueryParameters.Top = 50;
                        config.QueryParameters.Orderby = new[] { "receivedDateTime asc" };
                    },
                    ct
                );

            if (page?.Value != null)
                messages.AddRange(page.Value);

            // Follow @odata.nextLink pages.
            // nextLink URLs are opaque continuation tokens — do not set additional query params.
            while (page?.OdataNextLink != null)
            {
                page = await _graphClient
                    .Users[mailbox]
                    .MailFolders["inbox"]
                    .Messages.WithUrl(page.OdataNextLink)
                    .GetAsync(cancellationToken: ct);

                if (page?.Value != null)
                    messages.AddRange(page.Value);
            }

            _logger.LogDebug("Read {Count} inbox messages from {Mailbox}", messages.Count, mailbox);
            return messages;
        }

        public async Task<Message?> GetMessageWithAttachmentsAsync(
            string mailbox,
            string messageId,
            CancellationToken ct = default
        )
        {
            return await _graphClient
                .Users[mailbox]
                .Messages[messageId]
                .GetAsync(
                    config =>
                    {
                        config.QueryParameters.Expand = new[] { "attachments" };
                    },
                    ct
                );
        }

        public async Task MoveMessageAsync(
            string mailbox,
            string messageId,
            string destinationFolderId,
            CancellationToken ct = default
        )
        {
            await _graphClient
                .Users[mailbox]
                .Messages[messageId]
                .Move.PostAsync(
                    new MovePostRequestBody { DestinationId = destinationFolderId },
                    cancellationToken: ct
                );

            _logger.LogDebug(
                "Moved message {MessageId} to folder {FolderId} in {Mailbox}",
                messageId,
                destinationFolderId,
                mailbox
            );
        }

        public async Task<string> GetOrCreateFolderAsync(
            string mailbox,
            string folderName,
            CancellationToken ct = default
        )
        {
            // Search for existing folder by displayName
            var existing = await _graphClient
                .Users[mailbox]
                .MailFolders.GetAsync(
                    config =>
                    {
                        config.QueryParameters.Filter = $"displayName eq '{folderName.Replace("'", "''")}'";

                        config.QueryParameters.Select = new[] { "id", "displayName" };
                    },
                    ct
                );

            var folder = existing?.Value?.FirstOrDefault();
            if (folder != null)
                return folder.Id!;

            // Create the folder
            var created = await _graphClient
                .Users[mailbox]
                .MailFolders.PostAsync(
                    new MailFolder { DisplayName = folderName },
                    cancellationToken: ct
                );

            _logger.LogInformation(
                "Created mail folder '{FolderName}' (ID: {FolderId}) in {Mailbox}",
                folderName,
                created!.Id,
                mailbox
            );

            return created.Id!;
        }

        public async Task DeleteMessageRuleAsync(string mailbox, string ruleId, CancellationToken ct = default)
        {
            await _graphClient
                .Users[mailbox]
                .MailFolders["inbox"]
                .MessageRules[ruleId]
                .DeleteAsync(cancellationToken: ct);
        }

        public async Task<Message?> GetMessageByInternetIdAsync(
            string mailbox,
            string internetMessageId,
            CancellationToken ct = default
        )
        {
            // Filter by internetMessageId across all mail folders
            var result = await _graphClient
                .Users[mailbox]
                .Messages.GetAsync(
                    config =>
                    {
                        config.QueryParameters.Filter =
                            $"internetMessageId eq '{internetMessageId.Replace("'", "''")}'";
                        config.QueryParameters.Select = new[]
                        {
                            "id",
                            "internetMessageId",
                            "subject",
                            "from",
                            "receivedDateTime",
                            "body",
                            "hasAttachments",
                        };
                        config.QueryParameters.Top = 1;
                    },
                    ct
                );

            return result?.Value?.FirstOrDefault();
        }
    }
}
