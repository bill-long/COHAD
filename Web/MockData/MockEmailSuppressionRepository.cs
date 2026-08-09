#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Web.Models;
using Web.Services.Repositories;

namespace Web.MockData
{
    /// <summary>
    /// In-memory <see cref="IEmailSuppressionRepository"/> for the MockData environment and unit
    /// tests. Behaviourally identical to <see cref="CosmosEmailSuppressionRepository"/> per the
    /// repository conventions: the same conflict exception on a duplicate add and a stale-ETag
    /// update, the same ETag population on every path, the same active/all filtering, and clones
    /// on every path so callers cannot mutate the store.
    /// </summary>
    public sealed class MockEmailSuppressionRepository : IEmailSuppressionRepository
    {
        /// <summary>Ordinal keys, matching Cosmos document ids, which are case-sensitive.</summary>
        private readonly Dictionary<string, EmailSuppression> _items = new(StringComparer.Ordinal);
        private int _versionCounter;

        /// <summary>
        /// Seeds the MockData environment's sample suppression: Taylor's old address, hard-bounced
        /// during a seeded job, with the provider's own diagnostic text - so the Suppressions page,
        /// the address badge, and the enforcement skip are all exercisable without Cosmos. Called
        /// from the Startup registration rather than the constructor so unit tests get an empty
        /// store, matching the Cosmos implementation over an empty container.
        /// </summary>
        public MockEmailSuppressionRepository SeedSampleData()
        {
            var suppressedUtc = DateTime.UtcNow.AddDays(-10);
            var suppression = new EmailSuppression
            {
                Id = EmailSuppression.MakeId("taylor.old@cohad.local"),
                Email = "taylor.old@cohad.local",
                Reason = SuppressionReason.HardBounce,
                ConsecutiveFailureCount = 1,
                FirstSeenUtc = suppressedUtc,
                LastSeenUtc = suppressedUtc,
                CausingJobId = MockDataConstants.SampleEmailJobSeed3,
                SuppressedUtc = suppressedUtc,
                SuppressedBy = EmailSuppression.SystemDeliveryEvent,
                ProviderDiagnostic = "HardBounce: The server was unable to deliver your message (550 5.1.1 user unknown).",
            };

            lock (_items)
            {
                suppression.ETag = NextETag();
                _items[suppression.Id] = suppression;
            }

            return this;
        }

        public Task AddAsync(EmailSuppression suppression)
        {
            if (string.IsNullOrWhiteSpace(suppression.Id))
                throw new ArgumentException("EmailSuppression.Id must be set.", nameof(suppression));

            lock (_items)
            {
                if (_items.ContainsKey(suppression.Id))
                {
                    // Mirrors the Cosmos 409 → ConcurrencyConflictException mapping: the id is
                    // deterministic over the address, so a duplicate is a lost race to retry, not
                    // a collision to regenerate around.
                    throw new ConcurrencyConflictException(
                        "A suppression for this address already exists.",
                        new InvalidOperationException("Duplicate id in MockEmailSuppressionRepository.")
                    );
                }

                var clone = Clone(suppression);
                clone.ETag = NextETag();
                _items[suppression.Id] = clone;
                suppression.ETag = clone.ETag;
            }

            return Task.CompletedTask;
        }

        public Task UpdateAsync(EmailSuppression suppression)
        {
            if (string.IsNullOrWhiteSpace(suppression.Id))
                throw new ArgumentException("EmailSuppression.Id must be set.", nameof(suppression));
            if (string.IsNullOrWhiteSpace(suppression.ETag))
                throw new ArgumentException(
                    "EmailSuppression.ETag must be set for an update; read the record first.",
                    nameof(suppression)
                );

            lock (_items)
            {
                if (!_items.TryGetValue(suppression.Id, out var stored) || stored.ETag != suppression.ETag)
                {
                    // Mirrors the Cosmos 412 on a missing item or a stale ETag alike: Replace with
                    // IfMatch cannot succeed either way, and both mean "re-read and retry".
                    throw new ConcurrencyConflictException(
                        "The suppression was modified by another writer; re-read and retry.",
                        new InvalidOperationException("ETag mismatch in MockEmailSuppressionRepository.")
                    );
                }

                var clone = Clone(suppression);
                clone.ETag = NextETag();
                _items[suppression.Id] = clone;
                suppression.ETag = clone.ETag;
            }

            return Task.CompletedTask;
        }

        public Task<EmailSuppression?> GetByEmailAsync(string email)
        {
            if (string.IsNullOrWhiteSpace(email))
                return Task.FromResult<EmailSuppression?>(null);

            return GetByIdAsync(EmailSuppression.MakeId(email));
        }

        public Task<EmailSuppression?> GetByIdAsync(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return Task.FromResult<EmailSuppression?>(null);

            lock (_items)
            {
                return Task.FromResult(_items.TryGetValue(id, out var found) ? Clone(found) : null);
            }
        }

        public Task<List<EmailSuppression>> GetActiveAsync()
        {
            lock (_items)
            {
                return Task.FromResult(_items.Values.Where(s => s.IsActive).Select(Clone).ToList());
            }
        }

        public Task<List<EmailSuppression>> GetAllAsync()
        {
            lock (_items)
            {
                return Task.FromResult(_items.Values.Select(Clone).ToList());
            }
        }

        private string NextETag()
        {
            return Interlocked.Increment(ref _versionCounter).ToString();
        }

        private static EmailSuppression Clone(EmailSuppression s)
        {
            return new EmailSuppression
            {
                Id = s.Id,
                Email = s.Email,
                Reason = s.Reason,
                ConsecutiveFailureCount = s.ConsecutiveFailureCount,
                FirstSeenUtc = s.FirstSeenUtc,
                LastSeenUtc = s.LastSeenUtc,
                CausingJobId = s.CausingJobId,
                SuppressedUtc = s.SuppressedUtc,
                SuppressedBy = s.SuppressedBy,
                ProviderDiagnostic = s.ProviderDiagnostic,
                ClearedUtc = s.ClearedUtc,
                ClearedBy = s.ClearedBy,
                ETag = s.ETag,
            };
        }
    }
}
