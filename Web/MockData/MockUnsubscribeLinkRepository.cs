#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Web.Models;
using Web.Services.Repositories;

namespace Web.MockData
{
    /// <summary>
    /// In-memory <see cref="IUnsubscribeLinkRepository"/> for the MockData environment and unit tests.
    /// Behaviourally identical to <see cref="CosmosUnsubscribeLinkRepository"/> per the repository
    /// conventions: the same duplicate-id failure, the same ETag population on every path, and the
    /// same blank-id handling.
    /// </summary>
    public sealed class MockUnsubscribeLinkRepository : IUnsubscribeLinkRepository
    {
        /// <summary>
        /// Ordinal keys, matching Cosmos document ids, which are case-sensitive. A case-insensitive
        /// store here would resolve <c>/u/ABC</c> for a link issued as <c>/u/abc</c> and hide a real
        /// mismatch behind a mock more forgiving than production.
        /// </summary>
        private readonly Dictionary<string, UnsubscribeLink> _items = new(StringComparer.Ordinal);
        private int _versionCounter;

        public Task AddAsync(UnsubscribeLink link)
        {
            if (string.IsNullOrWhiteSpace(link.Id))
                throw new ArgumentException("UnsubscribeLink.Id must be set.", nameof(link));

            lock (_items)
            {
                if (_items.ContainsKey(link.Id))
                {
                    // Mirrors the Cosmos 409. The issuer treats this as a collision and generates a
                    // new id; a divergence here would let a collision path ship untested.
                    throw new DuplicateUnsubscribeLinkIdException(
                        "An unsubscribe link with this id already exists.",
                        new InvalidOperationException("Duplicate id in MockUnsubscribeLinkRepository.")
                    );
                }

                var clone = Clone(link);
                clone.ETag = Interlocked.Increment(ref _versionCounter).ToString();
                _items[link.Id] = clone;
                link.ETag = clone.ETag;
            }

            return Task.CompletedTask;
        }

        public Task<UnsubscribeLink?> GetByIdAsync(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return Task.FromResult<UnsubscribeLink?>(null);

            lock (_items)
            {
                return Task.FromResult(_items.TryGetValue(id, out var found) ? Clone(found) : null);
            }
        }

        private static UnsubscribeLink Clone(UnsubscribeLink l)
        {
            return new UnsubscribeLink
            {
                Id = l.Id,
                HomeId = l.HomeId,
                Email = l.Email,
                IssuedUtc = l.IssuedUtc,
                ETag = l.ETag,
            };
        }
    }
}
