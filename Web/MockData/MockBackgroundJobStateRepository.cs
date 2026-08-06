#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Web.Models;
using Web.Services.Repositories;

namespace Web.MockData
{
    /// <summary>In-memory <see cref="IBackgroundJobStateRepository"/> for the MockData environment and unit tests.</summary>
    public sealed class MockBackgroundJobStateRepository : IBackgroundJobStateRepository
    {
        private readonly Dictionary<string, BackgroundJobState> _items = new();
        private int _versionCounter;

        public Task<BackgroundJobState?> GetAsync(string jobName)
        {
            if (string.IsNullOrWhiteSpace(jobName))
                return Task.FromResult<BackgroundJobState?>(null);

            var key = BackgroundJobState.DeterministicId(jobName);
            lock (_items)
            {
                return Task.FromResult(_items.TryGetValue(key, out var found) ? Clone(found) : null);
            }
        }

        public Task UpsertAsync(BackgroundJobState state)
        {
            // Guard empty/whitespace names (mirrors GetAsync and the Cosmos impl): an empty name maps to
            // a shared key and would clobber unrelated jobs' pacing.
            if (string.IsNullOrWhiteSpace(state.JobName))
                return Task.CompletedTask;

            var key = BackgroundJobState.DeterministicId(state.JobName);
            lock (_items)
            {
                var clone = Clone(state);
                clone.JobName = key;
                clone.ETag = Interlocked.Increment(ref _versionCounter).ToString();
                _items[key] = clone;
                state.ETag = clone.ETag;
            }

            return Task.CompletedTask;
        }

        private static BackgroundJobState Clone(BackgroundJobState s)
        {
            return new BackgroundJobState
            {
                JobName = s.JobName,
                LastSuccessUtc = s.LastSuccessUtc,
                LastAttemptUtc = s.LastAttemptUtc,
                LastAttemptFailed = s.LastAttemptFailed,
                ETag = s.ETag,
            };
        }
    }
}
