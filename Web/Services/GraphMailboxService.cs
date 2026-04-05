using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Azure.Identity;
using Web.Models;

namespace Web.Services
{
    /// <summary>
    /// Manages inbox forwarding rules on O365 shared mailboxes via the Microsoft Graph API.
    /// Uses client-credentials (application permission) flow with <c>Mail.ReadWrite</c>.
    /// </summary>
    public sealed class GraphMailboxService : IGraphMailboxService
    {
        private readonly GraphServiceClient _graphClient;
        private readonly ILogger<GraphMailboxService> _logger;

        public GraphMailboxService(IConfiguration config, ILogger<GraphMailboxService> logger)
        {
            _logger = logger;

            var tenantId = config["Graph:TenantId"];
            var clientId = config["Graph:ClientId"];
            var clientSecret = config["Graph:ClientSecret"];

            var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
            _graphClient = new GraphServiceClient(credential);
        }

        public async Task<Committee> SyncForwardingRuleAsync(Committee committee,
            IReadOnlyDictionary<Guid, Resident> residents)
        {
            var recipients = (committee.Members ?? new List<CommitteeMember>())
                .Where(m => m.ReceivesForwardedEmail)
                .Select(m => residents.GetValueOrDefault(m.ResidentId))
                .Where(r => r?.EmailAddresses?.Any(e => !string.IsNullOrWhiteSpace(e.Address)) == true)
                .Select(r => new Recipient
                {
                    EmailAddress = new Microsoft.Graph.Models.EmailAddress
                    {
                        Address = r.EmailAddresses.First(e => !string.IsNullOrWhiteSpace(e.Address)).Address,
                        Name = $"{r.GivenName} {r.Surname}".Trim()
                    }
                })
                .ToList();

            var rule = new MessageRule
            {
                DisplayName = "COHAD Forwarding",
                IsEnabled = true,
                Sequence = 1,
                Actions = new MessageRuleActions
                {
                    ForwardTo = recipients
                }
            };

            try
            {
                if (!string.IsNullOrEmpty(committee.GraphMessageRuleId))
                {
                    // Update existing rule
                    await _graphClient.Users[committee.CommitteeEmail]
                        .MailFolders["inbox"]
                        .MessageRules[committee.GraphMessageRuleId]
                        .PatchAsync(rule);

                    _logger.LogInformation(
                        "Updated forwarding rule {RuleId} on {Mailbox}: {Count} recipients",
                        committee.GraphMessageRuleId, committee.CommitteeEmail, recipients.Count);
                }
                else
                {
                    // Create new rule
                    var created = await _graphClient.Users[committee.CommitteeEmail]
                        .MailFolders["inbox"]
                        .MessageRules
                        .PostAsync(rule);

                    committee.GraphMessageRuleId = created?.Id;

                    _logger.LogInformation(
                        "Created forwarding rule {RuleId} on {Mailbox}: {Count} recipients",
                        committee.GraphMessageRuleId, committee.CommitteeEmail, recipients.Count);
                }

                committee.LastSyncedUtc = DateTime.UtcNow;
                committee.LastSyncStatus = "Success";
                committee.LastSyncError = null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Graph API sync failed for {Mailbox}", committee.CommitteeEmail);
                committee.LastSyncedUtc = DateTime.UtcNow;
                committee.LastSyncStatus = "Failed";
                committee.LastSyncError = ex.Message;
                throw;
            }

            return committee;
        }
    }
}
