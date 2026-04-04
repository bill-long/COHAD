using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Web.Models;

namespace Web.MockData
{
    /// <summary>
    /// MockData implementation of <see cref="Services.IGraphMailboxService"/>.
    /// Logs the sync action and returns success without calling the Graph API.
    /// </summary>
    public sealed class MockGraphMailboxService : Services.IGraphMailboxService
    {
        private readonly ILogger<MockGraphMailboxService> _logger;

        public MockGraphMailboxService(ILogger<MockGraphMailboxService> logger)
        {
            _logger = logger;
        }

        public Task<Committee> SyncForwardingRuleAsync(Committee committee)
        {
            var recipients = committee.Members?
                .Where(m => m.ReceivesForwardedEmail)
                .Select(m => m.Email)
                .ToList() ?? new();

            _logger.LogInformation(
                "[MockGraph] Sync forwarding rule for {Email}: {Count} recipients ({Recipients})",
                committee.CommitteeEmail, recipients.Count, string.Join(", ", recipients));

            committee.GraphMessageRuleId ??= $"mock-rule-{committee.Id}";
            committee.LastSyncedUtc = DateTime.UtcNow;
            committee.LastSyncStatus = "Success";
            committee.LastSyncError = null;

            return Task.FromResult(committee);
        }
    }
}
