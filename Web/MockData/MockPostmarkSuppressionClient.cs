#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Web.Services;

namespace Web.MockData
{
    /// <summary>
    /// In-memory <see cref="IPostmarkSuppressionClient"/> for the MockData environment. Lets
    /// the suppression-dump reconciliation and the admin-clear reactivation run end to end
    /// without a Postmark account: the seeded dump entry is an address with no COHAD
    /// suppression, so the first sync run records it and the Manage &gt; Suppressions page shows
    /// the reconciler's own provenance (<c>postmark:suppression-dump</c>); clearing that record
    /// then deletes the dump entry here, so the next sync run does not re-suppress it - the
    /// same loop the real provider closes. Seeding is called from the Startup registration
    /// rather than the constructor so unit tests get an empty dump, matching
    /// <see cref="MockEmailSuppressionRepository"/>'s seeding convention.
    /// <para>
    /// Locked like the other MockData stores: the singleton is read by the suppression-sync
    /// background timer while an admin's clear mutates it from a request thread.
    /// </para>
    /// </summary>
    public sealed class MockPostmarkSuppressionClient : IPostmarkSuppressionClient
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, List<PostmarkSuppressionDumpEntry>> _dumpsByStream = new();

        public MockPostmarkSuppressionClient SeedSampleData()
        {
            lock (_gate)
            {
                _dumpsByStream["broadcast"] = new List<PostmarkSuppressionDumpEntry>
                {
                    new()
                    {
                        EmailAddress = "postmark.unsubscribed@cohad.local",
                        SuppressionReason = "ManualSuppression",
                        Origin = "Recipient",
                        CreatedAt = "2026-08-01T12:00:00-05:00",
                    },
                };
            }
            return this;
        }

        public Task<IReadOnlyList<PostmarkSuppressionDumpEntry>> GetSuppressionsAsync(
            string messageStream,
            CancellationToken cancellationToken
        )
        {
            // Behaviorally identical to the real client, per the mock-parity convention: the
            // same argument rejection, and the token is observed.
            if (string.IsNullOrWhiteSpace(messageStream))
                throw new ArgumentException("Message stream must not be empty.", nameof(messageStream));
            cancellationToken.ThrowIfCancellationRequested();

            // A copy, matching the mock repositories' clones-on-every-path convention: the caller
            // can never mutate the seeded store.
            lock (_gate)
            {
                return Task.FromResult<IReadOnlyList<PostmarkSuppressionDumpEntry>>(
                    _dumpsByStream.TryGetValue(messageStream, out var entries)
                        ? entries.ToList()
                        : new List<PostmarkSuppressionDumpEntry>()
                );
            }
        }

        public Task ReactivateAsync(
            string messageStream,
            string emailAddress,
            CancellationToken cancellationToken
        )
        {
            if (string.IsNullOrWhiteSpace(messageStream))
                throw new ArgumentException("Message stream must not be empty.", nameof(messageStream));
            if (string.IsNullOrWhiteSpace(emailAddress))
                throw new ArgumentException("Email address must not be empty.", nameof(emailAddress));
            cancellationToken.ThrowIfCancellationRequested();

            // Behaviorally identical to the provider: deleting an entry that does not exist is a
            // success, and the address match is case-insensitive on the trimmed value (Postmark
            // keys suppressions on the address, not on its casing).
            lock (_gate)
            {
                if (_dumpsByStream.TryGetValue(messageStream, out var entries))
                {
                    entries.RemoveAll(e =>
                        string.Equals(
                            e.EmailAddress.Trim(),
                            emailAddress.Trim(),
                            StringComparison.OrdinalIgnoreCase
                        )
                    );
                }
            }
            return Task.CompletedTask;
        }
    }
}
