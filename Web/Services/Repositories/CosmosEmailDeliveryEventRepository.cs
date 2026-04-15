#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json.Linq;
using Web.Models;
using CosmosContainer = Microsoft.Azure.Cosmos.Container;
using CosmosPartitionKey = Microsoft.Azure.Cosmos.PartitionKey;
using CosmosQueryDefinition = Microsoft.Azure.Cosmos.QueryDefinition;

namespace Web.Services.Repositories
{
    public class CosmosEmailDeliveryEventRepository : IEmailDeliveryEventRepository
    {
        private readonly CosmosContainer _container;

        public CosmosEmailDeliveryEventRepository(CosmosContainer container)
        {
            _container = container;
        }

        /// <summary>
        /// TTL for delivery event documents (120 days in seconds).
        /// Provides a safety net so late/orphaned events are auto-purged even if
        /// the parent job has already been cleaned up by retention.
        /// </summary>
        private const int DocumentTtlSeconds = 120 * 24 * 60 * 60; // 120 days

        public async Task AddAsync(EmailDeliveryEvent deliveryEvent)
        {
            var pk = new CosmosPartitionKey(deliveryEvent.JobId.ToString("D"));
            var doc = ToDocument(deliveryEvent);

            // Try to create the document. If it already exists, fall through to
            // a targeted patch that updates webhook-owned fields and allows a
            // one-way false→true transition for ActionProcessed (processor-owned).
            try
            {
                await _container.CreateItemAsync(doc, pk);
                return;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
            {
                // Document already exists — patch selectively below
            }

            var patchOps = new List<PatchOperation>
            {
                PatchOperation.Set("/deliveryStatus", deliveryEvent.DeliveryStatus.ToString()),
                PatchOperation.Set("/provider", deliveryEvent.Provider),
                PatchOperation.Set("/receivedUtc", deliveryEvent.ReceivedUtc),
                PatchOperation.Set("/ttl", DocumentTtlSeconds),
            };

            // Only update providerMessageId if the new value is non-empty
            if (!string.IsNullOrEmpty(deliveryEvent.ProviderMessageId))
            {
                patchOps.Add(PatchOperation.Set("/providerMessageId", deliveryEvent.ProviderMessageId));
            }

            // Allow one-way transition: false→true only (processor marks processed)
            if (deliveryEvent.ActionProcessed)
            {
                patchOps.Add(PatchOperation.Set("/actionProcessed", true));
            }

            try
            {
                await _container.PatchItemAsync<JObject>(deliveryEvent.Id, pk, patchOps);
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // Document deleted between Create conflict and Patch (e.g., retention
                // cleanup raced). Retry create to keep AddAsync idempotent.
                try
                {
                    await _container.CreateItemAsync(doc, pk);
                }
                catch (CosmosException createEx) when (createEx.StatusCode == HttpStatusCode.Conflict)
                {
                    // Another concurrent writer recreated it — treat as success.
                }
            }
        }

        public async Task<List<EmailDeliveryEvent>> GetByJobIdAsync(Guid jobId)
        {
            var query = new CosmosQueryDefinition(
                "SELECT * FROM c WHERE c.jobId = @jobId ORDER BY c.receivedUtc ASC"
            ).WithParameter("@jobId", jobId.ToString("D"));

            var pk = new CosmosPartitionKey(jobId.ToString("D"));
            var iterator = _container.GetItemQueryIterator<JObject>(
                query,
                requestOptions: new QueryRequestOptions { PartitionKey = pk }
            );

            var results = new List<EmailDeliveryEvent>();
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                results.AddRange(response.Select(FromDocument));
            }

            return results;
        }

        public async Task DeleteByJobIdAsync(Guid jobId)
        {
            var pk = new CosmosPartitionKey(jobId.ToString("D"));
            var query = new CosmosQueryDefinition("SELECT c.id FROM c WHERE c.jobId = @jobId").WithParameter(
                "@jobId",
                jobId.ToString("D")
            );

            var iterator = _container.GetItemQueryIterator<JObject>(
                query,
                requestOptions: new QueryRequestOptions { PartitionKey = pk }
            );

            var batchIds = new List<string>(100);

            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                foreach (var item in response)
                {
                    var docId = item.Value<string>("id");
                    if (string.IsNullOrEmpty(docId))
                        continue;

                    batchIds.Add(docId);
                    if (batchIds.Count == 100)
                    {
                        await DeleteBatchAsync(pk, batchIds);
                        batchIds.Clear();
                    }
                }
            }

            if (batchIds.Count > 0)
            {
                await DeleteBatchAsync(pk, batchIds);
            }
        }

        private async Task DeleteBatchAsync(CosmosPartitionKey pk, IReadOnlyList<string> documentIds)
        {
            var batch = _container.CreateTransactionalBatch(pk);
            foreach (var documentId in documentIds)
            {
                batch.DeleteItem(documentId);
            }

            using var response = await batch.ExecuteAsync();
            if (response.IsSuccessStatusCode)
                return;

            // Batch failed — fall back to individual deletes
            foreach (var documentId in documentIds)
            {
                try
                {
                    await _container.DeleteItemAsync<JObject>(documentId, pk);
                }
                catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    // Already deleted — idempotent
                }
            }
        }

        private static JObject ToDocument(EmailDeliveryEvent e) =>
            new()
            {
                ["id"] = e.Id,
                ["jobId"] = e.JobId.ToString("D"),
                ["email"] = e.Email,
                ["deliveryStatus"] = e.DeliveryStatus.ToString(),
                ["providerMessageId"] = e.ProviderMessageId,
                ["provider"] = e.Provider,
                ["receivedUtc"] = e.ReceivedUtc,
                ["actionProcessed"] = e.ActionProcessed,
                ["ttl"] = DocumentTtlSeconds,
            };

        private static EmailDeliveryEvent FromDocument(JObject doc) =>
            new()
            {
                Id = doc.Value<string>("id") ?? "",
                JobId = Guid.TryParse(doc.Value<string>("jobId"), out var jid) ? jid : Guid.Empty,
                Email = doc.Value<string>("email") ?? "",
                DeliveryStatus = Enum.TryParse<DeliveryStatus>(doc.Value<string>("deliveryStatus"), out var ds)
                    ? ds
                    : DeliveryStatus.Unknown,
                ProviderMessageId = doc.Value<string>("providerMessageId"),
                Provider = doc.Value<string>("provider"),
                ReceivedUtc = doc.Value<DateTime?>("receivedUtc") ?? DateTime.MinValue,
                ActionProcessed = doc.Value<bool?>("actionProcessed") ?? false,
            };
    }
}
