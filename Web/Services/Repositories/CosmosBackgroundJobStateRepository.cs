#nullable enable
using System;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json.Linq;
using Web.Models;
using CosmosContainer = Microsoft.Azure.Cosmos.Container;
using CosmosPartitionKey = Microsoft.Azure.Cosmos.PartitionKey;

namespace Web.Services.Repositories
{
    public interface IBackgroundJobStateRepository
    {
        /// <summary>Returns the job's run state, or null if it has never run.</summary>
        Task<BackgroundJobState?> GetAsync(string jobName);

        /// <summary>Creates or replaces the job's run state.</summary>
        Task UpsertAsync(BackgroundJobState state);
    }

    public class CosmosBackgroundJobStateRepository : IBackgroundJobStateRepository
    {
        private readonly CosmosContainer _container;

        public CosmosBackgroundJobStateRepository(CosmosContainer container)
        {
            _container = container;
        }

        public async Task<BackgroundJobState?> GetAsync(string jobName)
        {
            if (string.IsNullOrWhiteSpace(jobName))
                return null;

            // Point read by the job's deterministic id - cheaper than a query and reliably reads the
            // state written by the previous run.
            try
            {
                var docId = ToDocumentId(BackgroundJobState.DeterministicId(jobName));
                var response = await _container.ReadItemAsync<JObject>(docId, CosmosPartitionKey.None);
                var state = ToState(response.Resource);
                state.ETag = response.Headers.ETag;
                return state;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound && ex.SubStatusCode == 0)
            {
                // Only a genuinely missing document maps to "this job has never run". A 404 with a
                // non-zero sub-status (e.g. the container was never provisioned) is a misconfiguration,
                // so let it surface. Swallowing it would make every tick look like a first run, so an
                // interval-paced job would fire on every single app start instead of on its cadence.
                return null;
            }
        }

        public async Task UpsertAsync(BackgroundJobState state)
        {
            // Guard empty/whitespace names (mirrors GetAsync): an empty name would write a shared
            // document id and let unrelated jobs clobber each other's pacing.
            if (string.IsNullOrWhiteSpace(state.JobName))
                return;

            var response = await _container.UpsertItemAsync(ToDocument(state), CosmosPartitionKey.None);
            state.ETag = response.Headers.ETag;
        }

        private static string ToDocumentId(string jobName) => $"BackgroundJobState|{jobName}";

        private static BackgroundJobState ToState(JObject doc)
        {
            return new BackgroundJobState
            {
                JobName = doc.Value<string>("JobName") ?? string.Empty,
                LastSuccessUtc = doc["LastSuccessUtc"]?.ToObject<DateTime>() ?? DateTime.MinValue,
                LastAttemptUtc = doc["LastAttemptUtc"]?.ToObject<DateTime>() ?? DateTime.MinValue,
                ETag = doc.Value<string>("_etag"),
            };
        }

        private static JObject ToDocument(BackgroundJobState s)
        {
            var normalized = BackgroundJobState.DeterministicId(s.JobName);
            return new JObject
            {
                ["id"] = ToDocumentId(normalized),
                ["Discriminator"] = "BackgroundJobState",
                ["JobName"] = normalized,
                ["LastSuccessUtc"] = JToken.FromObject(s.LastSuccessUtc),
                ["LastAttemptUtc"] = JToken.FromObject(s.LastAttemptUtc),
            };
        }
    }
}
