#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Web.Models;
using Web.Services.Repositories;

namespace Web.MockData
{
    public sealed class MockEmailDeliveryEventRepository : IEmailDeliveryEventRepository
    {
        private readonly Dictionary<string, EmailDeliveryEvent> _events = new(StringComparer.OrdinalIgnoreCase);

        public Task AddAsync(EmailDeliveryEvent deliveryEvent)
        {
            lock (_events)
            {
                // Merge-preserving upsert: preserve processor-owned fields
                if (_events.TryGetValue(deliveryEvent.Id, out var existing))
                {
                    if (existing.ActionProcessed)
                        deliveryEvent.ActionProcessed = true;
                    if (
                        string.IsNullOrEmpty(deliveryEvent.ProviderMessageId)
                        && !string.IsNullOrEmpty(existing.ProviderMessageId)
                    )
                        deliveryEvent.ProviderMessageId = existing.ProviderMessageId;
                }

                _events[deliveryEvent.Id] = Clone(deliveryEvent);
            }

            return Task.CompletedTask;
        }

        public Task<List<EmailDeliveryEvent>> GetByJobIdAsync(Guid jobId)
        {
            lock (_events)
            {
                var list = _events
                    .Values.Where(e => e.JobId == jobId)
                    .OrderBy(e => e.ReceivedUtc)
                    .Select(Clone)
                    .ToList();
                return Task.FromResult(list);
            }
        }

        public Task DeleteByJobIdAsync(Guid jobId)
        {
            lock (_events)
            {
                var toRemove = _events.Where(kv => kv.Value.JobId == jobId).Select(kv => kv.Key).ToList();
                foreach (var key in toRemove)
                    _events.Remove(key);
            }

            return Task.CompletedTask;
        }

        private static EmailDeliveryEvent Clone(EmailDeliveryEvent e) =>
            new()
            {
                Id = e.Id,
                JobId = e.JobId,
                Email = e.Email,
                DeliveryStatus = e.DeliveryStatus,
                ProviderMessageId = e.ProviderMessageId,
                Provider = e.Provider,
                ReceivedUtc = e.ReceivedUtc,
                ActionProcessed = e.ActionProcessed,
            };
    }
}
