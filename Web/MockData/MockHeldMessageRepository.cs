#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Web.Models;
using Web.Services.Repositories;

namespace Web.MockData
{
    /// <summary>
    /// In-memory held message store for MockData environment.
    /// </summary>
    public sealed class MockHeldMessageRepository : IHeldMessageRepository
    {
        private readonly Dictionary<Guid, HeldMessage> _messages = new();

        public Task AddAsync(HeldMessage message)
        {
            lock (_messages)
            {
                message.ETag = Guid.NewGuid().ToString();
                _messages[message.Id] = Clone(message);
            }

            return Task.CompletedTask;
        }

        public Task<HeldMessage?> GetByIdAsync(Guid id)
        {
            lock (_messages)
            {
                return Task.FromResult(
                    _messages.TryGetValue(id, out var msg) ? Clone(msg) : null
                );
            }
        }

        public Task<HeldMessage?> GetByGraphMessageIdAsync(string committeeId, string graphMessageId)
        {
            if (string.IsNullOrEmpty(graphMessageId))
                return Task.FromResult<HeldMessage?>(null);

            lock (_messages)
            {
                var match = _messages.Values.FirstOrDefault(m =>
                    m.CommitteeId == committeeId && m.GraphMessageId == graphMessageId
                );
                return Task.FromResult(match != null ? Clone(match) : null);
            }
        }

        public Task UpdateAsync(HeldMessage message)
        {
            lock (_messages)
            {
                if (!_messages.ContainsKey(message.Id))
                    throw new InvalidOperationException($"HeldMessage {message.Id} not found.");
                message.ETag = Guid.NewGuid().ToString();
                _messages[message.Id] = Clone(message);
            }

            return Task.CompletedTask;
        }

        public Task<List<HeldMessage>> GetByCommitteeIdAsync(string committeeId, int limit = 50)
        {
            lock (_messages)
            {
                var list = _messages
                    .Values.Where(m => m.CommitteeId == committeeId)
                    .OrderByDescending(m => m.HeldUtc)
                    .Take(Math.Clamp(limit, 1, 200))
                    .Select(Clone)
                    .ToList();
                return Task.FromResult(list);
            }
        }

        public Task<List<HeldMessage>> GetExpiredAsync(DateTime cutoffUtc, int limit = 100)
        {
            lock (_messages)
            {
                var list = _messages
                    .Values.Where(m => m.Status == HeldMessageStatus.Held && m.HeldUtc < cutoffUtc)
                    .OrderBy(m => m.HeldUtc)
                    .Take(Math.Clamp(limit, 1, 250))
                    .Select(Clone)
                    .ToList();
                return Task.FromResult(list);
            }
        }

        private static HeldMessage Clone(HeldMessage m) =>
            new()
            {
                Id = m.Id,
                CommitteeId = m.CommitteeId,
                CommitteeEmail = m.CommitteeEmail,
                GraphMessageId = m.GraphMessageId,
                SenderEmail = m.SenderEmail,
                SenderName = m.SenderName,
                Subject = m.Subject,
                ReceivedUtc = m.ReceivedUtc,
                HeldUtc = m.HeldUtc,
                Status = m.Status,
                ReviewedByUserId = m.ReviewedByUserId,
                ReviewedUtc = m.ReviewedUtc,
                ETag = m.ETag,
            };
    }
}
