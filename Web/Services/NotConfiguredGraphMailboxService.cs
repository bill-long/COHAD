using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Web.Models;

namespace Web.Services
{
    /// <summary>
    /// Fallback when Graph API credentials are not configured.
    /// Returns a clear "NotConfigured" status instead of pretending success.
    /// </summary>
    internal sealed class NotConfiguredGraphMailboxService : IGraphMailboxService
    {
        private readonly ILogger<NotConfiguredGraphMailboxService> _logger;

        public NotConfiguredGraphMailboxService(ILogger<NotConfiguredGraphMailboxService> logger)
        {
            _logger = logger;
        }

        public Task<Committee> SyncForwardingRuleAsync(
            Committee committee,
            IReadOnlyDictionary<Guid, Resident> residents
        )
        {
            _logger.LogWarning(
                "Graph API credentials are not configured. Forwarding sync for {Email} skipped.",
                committee.CommitteeEmail
            );

            committee.LastSyncedUtc = DateTime.UtcNow;
            committee.LastSyncStatus = "NotConfigured";
            committee.LastSyncError =
                "Graph API credentials (Graph:TenantId, Graph:ClientId, Graph:ClientSecret) are not configured.";

            return Task.FromResult(committee);
        }
    }
}
