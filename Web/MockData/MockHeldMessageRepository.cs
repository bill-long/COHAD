#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
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

        /// <summary>
        /// Seeds one held (non-directory) message awaiting moderation in the Board committee's
        /// Approvals inbox, so the approve flow - including the suppression-aware recipient selection
        /// it triggers - is exercisable in MockData. Nothing else in this environment can create a
        /// held message: only the mail poller holds mail, and it requires Graph credentials. Already
        /// notified (<see cref="HeldMessage.NotifiedUtc"/> set) so it renders as an ordinary
        /// actionable item rather than one still in the antispam quarantine window. Called from the
        /// Startup registration rather than the constructor so unit tests get an empty store.
        /// <para>
        /// The suppression-specific demo lives in a Board <em>broadcast</em>, where Taylor's
        /// seeded-suppressed <c>taylor.old@cohad.local</c> is its own recipient and the enforcement
        /// point marks it Suppressed on the job detail; the deliverable-address forwarding preference
        /// is covered by unit tests rather than by ordering a hidden address first in this seed.
        /// </para>
        /// </summary>
        public MockHeldMessageRepository SeedSampleData()
        {
            var heldUtc = DateTime.UtcNow.AddHours(-3);
            var held = new HeldMessage
            {
                Id = MockDataConstants.SampleHeldMessageId,
                CommitteeId = "board",
                CommitteeEmail = "board@cohad.org",
                InternetMessageId = "<mock-held-1@example.com>",
                SenderEmail = "vendor@example.com",
                SenderName = "Outside Vendor",
                Subject = "Quote for common-area landscaping",
                ReceivedUtc = heldUtc,
                HeldUtc = heldUtc,
                NotifiedUtc = heldUtc.AddHours(1),
                Status = HeldMessageStatus.Held,
            };

            lock (_messages)
            {
                held.ETag = Guid.NewGuid().ToString();
                _messages[held.Id] = held;
            }

            return this;
        }

        public Task AddAsync(HeldMessage message)
        {
            lock (_messages)
            {
                if (_messages.ContainsKey(message.Id))
                {
                    // Mirror Cosmos CreateItemAsync, which fails with 409 on a duplicate id. The mail
                    // poller assigns deterministic ids and relies on this to dedup concurrent holds.
                    throw new CosmosException("HeldMessage already exists.", HttpStatusCode.Conflict, 0, string.Empty, 0);
                }

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

        public Task<HeldMessage?> GetByInternetMessageIdAsync(string committeeId, string internetMessageId)
        {
            if (string.IsNullOrEmpty(internetMessageId))
                return Task.FromResult<HeldMessage?>(null);

            lock (_messages)
            {
                var match = _messages.Values.FirstOrDefault(m =>
                    m.CommitteeId == committeeId && m.InternetMessageId == internetMessageId
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

                var stored = _messages[message.Id];
                if (!string.IsNullOrEmpty(message.ETag) && stored.ETag != message.ETag)
                    throw new InvalidOperationException("HeldMessage was modified by another process.");

                message.ETag = Guid.NewGuid().ToString();
                _messages[message.Id] = Clone(message);
            }

            return Task.CompletedTask;
        }

        public Task<List<HeldMessage>> GetByCommitteeIdAsync(string committeeId, int limit = 50, HeldMessageStatus? status = null)
        {
            lock (_messages)
            {
                var list = _messages
                    .Values.Where(m => m.CommitteeId == committeeId && (!status.HasValue || m.Status == status.Value))
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

        public Task<List<HeldMessage>> GetAwaitingNotificationAsync(DateTime heldBeforeUtc, int limit = 100)
        {
            lock (_messages)
            {
                var list = _messages
                    .Values.Where(m =>
                        m.Status == HeldMessageStatus.Held && m.NotifiedUtc == null && m.HeldUtc <= heldBeforeUtc
                    )
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
                InternetMessageId = m.InternetMessageId,
                SenderEmail = m.SenderEmail,
                SenderName = m.SenderName,
                Subject = m.Subject,
                ReceivedUtc = m.ReceivedUtc,
                HeldUtc = m.HeldUtc,
                NotifiedUtc = m.NotifiedUtc,
                Status = m.Status,
                SpamVerdict = m.SpamVerdict,
                SpamConfidence = m.SpamConfidence,
                SpamReason = m.SpamReason,
                ReviewedByUserId = m.ReviewedByUserId,
                ReviewedUtc = m.ReviewedUtc,
                ETag = m.ETag,
            };
    }
}
