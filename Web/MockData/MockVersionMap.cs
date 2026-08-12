using System;
using System.Collections.Generic;
using Web.Services.Repositories;

namespace Web.MockData
{
    /// <summary>
    /// Simulates Cosmos DB ETag semantics for the in-memory Mock repositories, so optimistic
    /// concurrency behaves identically between Mock and Cosmos (the repository convention) and the
    /// simulation is defined in exactly one place. Versions come from a single monotonic counter
    /// shared across all keys, so an ETag can never recur - in particular, a document deleted and
    /// recreated gets a fresh ETag, and a stale pre-delete ETag can never falsely match again.
    /// Not thread-safe by itself: callers must invoke every member under the same lock that guards
    /// the owning repository's store.
    /// </summary>
    internal sealed class MockVersionMap<TKey>
    {
        private readonly Dictionary<TKey, long> _versions;
        private long _lastVersion;

        internal MockVersionMap(IEqualityComparer<TKey> comparer = null)
        {
            _versions = new Dictionary<TKey, long>(comparer);
        }

        /// <summary>Current ETag for the key, or null when the key has never been written.</summary>
        internal string GetETag(TKey key) => _versions.TryGetValue(key, out var v) ? v.ToString() : null;

        /// <summary>
        /// Enforces the optimistic-concurrency check for a write. A null/empty ETag is a blind
        /// write (Cosmos sends no If-Match) and always passes. A supplied ETag asserts "this write
        /// continues from a version I read": it conflicts when it does not match the current
        /// version, and also when the key has no version at all - the document was deleted after
        /// the read, and writing would silently resurrect it (the Cosmos repositories surface that
        /// case as the same conflict). <paramref name="subject"/> is the short record noun
        /// ("User", "Home") handed to <see cref="ConcurrencyConflictException.For"/>.
        /// </summary>
        internal void ThrowIfStale(TKey key, string etag, string subject)
        {
            if (string.IsNullOrEmpty(etag))
            {
                return;
            }

            if (!_versions.TryGetValue(key, out var current) || etag != current.ToString())
            {
                throw ConcurrencyConflictException.For(subject, key, new InvalidOperationException("ETag mismatch"));
            }
        }

        /// <summary>Records a successful write and returns the key's fresh ETag.</summary>
        internal string Advance(TKey key)
        {
            _versions[key] = ++_lastVersion;
            return _versions[key].ToString();
        }

        /// <summary>Forgets a deleted key. The retired versions are never reissued.</summary>
        internal void Remove(TKey key) => _versions.Remove(key);
    }
}
