#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Web.Services;

namespace Web.MockData
{
    /// <summary>
    /// In-memory <see cref="IPostmarkSuppressionDumpClient"/> for the MockData environment. Lets
    /// the suppression-dump reconciliation run end to end without a Postmark account: the seeded
    /// dump entry is an address with no COHAD suppression, so the first sync run records it and
    /// the Manage &gt; Suppressions page shows the reconciler's own provenance
    /// (<c>postmark:suppression-dump</c>). Called from the Startup registration rather than the
    /// constructor so unit tests get an empty dump, matching
    /// <see cref="MockEmailSuppressionRepository"/>'s seeding convention.
    /// </summary>
    public sealed class MockPostmarkSuppressionDumpClient : IPostmarkSuppressionDumpClient
    {
        private readonly Dictionary<string, List<PostmarkSuppressionDumpEntry>> _dumpsByStream = new();

        public MockPostmarkSuppressionDumpClient SeedSampleData()
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
            return this;
        }

        public Task<IReadOnlyList<PostmarkSuppressionDumpEntry>> GetSuppressionsAsync(
            string messageStream,
            CancellationToken cancellationToken
        )
        {
            // A copy, matching the mock repositories' clones-on-every-path convention: the caller
            // can never mutate the seeded store.
            return Task.FromResult<IReadOnlyList<PostmarkSuppressionDumpEntry>>(
                _dumpsByStream.TryGetValue(messageStream, out var entries)
                    ? entries.ToList()
                    : new List<PostmarkSuppressionDumpEntry>()
            );
        }
    }
}
