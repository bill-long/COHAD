#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json.Linq;
using Web.Models;
using Web.Services.Cosmos;
using CosmosContainer = Microsoft.Azure.Cosmos.Container;
using CosmosPartitionKey = Microsoft.Azure.Cosmos.PartitionKey;
using CosmosQueryDefinition = Microsoft.Azure.Cosmos.QueryDefinition;

namespace Web.Services.Repositories
{
    public class CosmosEmailJobRepository : IEmailJobRepository
    {
        private readonly CosmosContainer _emailJobContainer;

        public CosmosEmailJobRepository(CosmosContainer emailJobContainer)
        {
            _emailJobContainer = emailJobContainer;
        }

        public async Task AddAsync(EmailJob job)
        {
            var doc = CosmosLegacyDocumentMapper.ToEmailJobDocument(job);
            var response = await _emailJobContainer.CreateItemAsync(doc, CosmosPartitionKey.None);
            job.ETag = response.Headers.ETag;
        }

        public async Task<EmailJob?> GetByIdAsync(Guid jobId)
        {
            var documentId = CosmosLegacyDocumentMapper.ToEmailJobDocumentId(jobId);
            try
            {
                var response = await _emailJobContainer.ReadItemAsync<JObject>(documentId, CosmosPartitionKey.None);
                var job = CosmosLegacyDocumentMapper.ToEmailJob(response.Resource);
                job.ETag = response.Headers.ETag;
                return job;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        public async Task DeleteAsync(Guid jobId)
        {
            var documentId = CosmosLegacyDocumentMapper.ToEmailJobDocumentId(jobId);
            try
            {
                await _emailJobContainer.DeleteItemAsync<JObject>(documentId, CosmosPartitionKey.None);
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // Idempotent cleanup
            }
        }

        public async Task UpdateAsync(EmailJob job)
        {
            var doc = CosmosLegacyDocumentMapper.ToEmailJobDocument(job);
            var documentId = doc.Value<string>("id");
            var requestOptions = new ItemRequestOptions();
            if (!string.IsNullOrEmpty(job.ETag))
                requestOptions.IfMatchEtag = job.ETag;

            try
            {
                var response = await _emailJobContainer.ReplaceItemAsync(
                    doc,
                    documentId,
                    CosmosPartitionKey.None,
                    requestOptions
                );
                job.ETag = response.Headers.ETag;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                throw new EmailJobConcurrencyException();
            }
        }

        public async Task<bool> TryClaimAsync(EmailJob job)
        {
            var doc = CosmosLegacyDocumentMapper.ToEmailJobDocument(job);
            var documentId = doc.Value<string>("id");
            var requestOptions = new ItemRequestOptions();
            if (!string.IsNullOrEmpty(job.ETag))
            {
                requestOptions.IfMatchEtag = job.ETag;
            }

            try
            {
                var response = await _emailJobContainer.ReplaceItemAsync(
                    doc,
                    documentId,
                    CosmosPartitionKey.None,
                    requestOptions
                );
                job.ETag = response.Headers.ETag;
                return true;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                return false;
            }
        }

        public async Task<List<EmailJob>> GetIncompleteJobsAsync()
        {
            var query = new CosmosQueryDefinition(
                "SELECT * FROM c WHERE c.Status IN (@queued, @inProgress) ORDER BY c.CreatedUtc ASC"
            )
                .WithParameter("@queued", nameof(EmailJobStatus.Queued))
                .WithParameter("@inProgress", nameof(EmailJobStatus.InProgress));

            var iterator = _emailJobContainer.GetItemQueryIterator<JObject>(query);
            var results = new List<EmailJob>();
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                results.AddRange(response.Select(CosmosLegacyDocumentMapper.ToEmailJob));
            }

            return results;
        }

        public async Task<List<EmailJob>> GetRecentJobsAsync(int limit)
        {
            var clampedLimit = Math.Clamp(limit, 1, 100);
            var query = new CosmosQueryDefinition($"SELECT TOP {clampedLimit} * FROM c ORDER BY c.CreatedUtc DESC");

            var iterator = _emailJobContainer.GetItemQueryIterator<JObject>(query);
            var results = new List<EmailJob>();
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                results.AddRange(response.Select(CosmosLegacyDocumentMapper.ToEmailJob));
            }

            return results;
        }

        public async Task<List<EmailJob>> GetTerminalJobsOlderThanAsync(DateTime cutoffUtc, int limit)
        {
            var clampedLimit = Math.Clamp(limit, 1, 250);

            // Cosmos stores status as strings; use explicit terminal statuses so we never delete active work.
            var query = new CosmosQueryDefinition(
                $"SELECT TOP {clampedLimit} * FROM c "
                    + "WHERE c.CreatedUtc < @cutoffUtc AND c.Status IN (@completed, @partial, @failed, @cancelled) "
                    + "ORDER BY c.CreatedUtc ASC"
            )
                .WithParameter("@cutoffUtc", cutoffUtc)
                .WithParameter("@completed", nameof(EmailJobStatus.Completed))
                .WithParameter("@partial", nameof(EmailJobStatus.PartiallyCompleted))
                .WithParameter("@failed", nameof(EmailJobStatus.Failed))
                .WithParameter("@cancelled", nameof(EmailJobStatus.Cancelled));

            var iterator = _emailJobContainer.GetItemQueryIterator<JObject>(query);
            var results = new List<EmailJob>();
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                results.AddRange(response.Select(CosmosLegacyDocumentMapper.ToEmailJob));
            }

            return results;
        }

        public async Task<List<EmailJob>> GetRecentlyCompletedJobsAsync(DateTime completedAfterUtc, int limit)
        {
            var clampedLimit = Math.Clamp(limit, 1, IEmailJobRepository.MaxRecentlyCompletedJobsLimit);

            // Return terminal jobs (not Cancelled — those have no meaningful delivery events)
            // that completed within the given window, newest first so a bounded
            // result set doesn't repeatedly surface the oldest completions.
            var query = new CosmosQueryDefinition(
                $"SELECT TOP {clampedLimit} * FROM c "
                    + "WHERE c.CompletedUtc >= @completedAfterUtc AND c.Status IN (@completed, @partial, @failed) "
                    + "ORDER BY c.CompletedUtc DESC"
            )
                .WithParameter("@completedAfterUtc", completedAfterUtc)
                .WithParameter("@completed", nameof(EmailJobStatus.Completed))
                .WithParameter("@partial", nameof(EmailJobStatus.PartiallyCompleted))
                .WithParameter("@failed", nameof(EmailJobStatus.Failed));

            var iterator = _emailJobContainer.GetItemQueryIterator<JObject>(query);
            var results = new List<EmailJob>();
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                results.AddRange(response.Select(CosmosLegacyDocumentMapper.ToEmailJob));
            }

            return results;
        }

        public async Task<EmailJob?> GetByInternetMessageIdAsync(string internetMessageId, string fromEmail)
        {
            if (string.IsNullOrEmpty(internetMessageId))
                return null;

            var query = new CosmosQueryDefinition(
                "SELECT TOP 1 * FROM c WHERE c.InternetMessageId = @internetMessageId AND c.FromEmail = @fromEmail"
            )
                .WithParameter("@internetMessageId", internetMessageId)
                .WithParameter("@fromEmail", fromEmail);

            var iterator = _emailJobContainer.GetItemQueryIterator<JObject>(query);
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                var first = response.FirstOrDefault();
                if (first != null)
                {
                    return CosmosLegacyDocumentMapper.ToEmailJob(first);
                }
            }

            return null;
        }
    }
}
