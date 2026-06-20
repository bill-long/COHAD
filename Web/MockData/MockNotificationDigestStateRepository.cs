#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Web.Models;
using Web.Services.Repositories;

namespace Web.MockData
{
    /// <summary>In-memory <see cref="INotificationDigestStateRepository"/> for the MockData environment and unit tests.</summary>
    public sealed class MockNotificationDigestStateRepository : INotificationDigestStateRepository
    {
        private readonly Dictionary<string, NotificationDigestState> _items = new();
        private int _versionCounter;

        public Task<NotificationDigestState?> GetAsync(string recipientEmail)
        {
            if (string.IsNullOrWhiteSpace(recipientEmail))
                return Task.FromResult<NotificationDigestState?>(null);

            var key = NotificationDigestState.DeterministicId(recipientEmail);
            lock (_items)
            {
                return Task.FromResult(_items.TryGetValue(key, out var found) ? Clone(found) : null);
            }
        }

        public Task UpsertAsync(NotificationDigestState state)
        {
            // Guard empty/whitespace emails (mirrors GetAsync and the Cosmos impl): an empty email
            // hashes to a shared deterministic id and would clobber unrelated throttle state.
            if (string.IsNullOrWhiteSpace(state.RecipientEmail))
                return Task.CompletedTask;

            var key = NotificationDigestState.DeterministicId(state.RecipientEmail);
            lock (_items)
            {
                var clone = Clone(state);
                clone.RecipientEmail = (state.RecipientEmail ?? string.Empty).Trim().ToLowerInvariant();
                clone.ETag = Interlocked.Increment(ref _versionCounter).ToString();
                _items[key] = clone;
                state.ETag = clone.ETag;
            }

            return Task.CompletedTask;
        }

        private static NotificationDigestState Clone(NotificationDigestState s)
        {
            return new NotificationDigestState
            {
                RecipientEmail = s.RecipientEmail,
                LastDigestUtc = s.LastDigestUtc,
                ETag = s.ETag,
            };
        }
    }
}
