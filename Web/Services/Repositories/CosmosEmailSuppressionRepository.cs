#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json.Linq;
using Web.Models;
using CosmosContainer = Microsoft.Azure.Cosmos.Container;
using CosmosPartitionKey = Microsoft.Azure.Cosmos.PartitionKey;

namespace Web.Services.Repositories
{
    public interface IEmailSuppressionRepository
    {
        /// <summary>
        /// Creates the record, populating its <see cref="EmailSuppression.ETag"/> from the
        /// response. Throws <see cref="ConcurrencyConflictException"/> when a record for the
        /// address already exists: the id is deterministic over the address, so a duplicate means
        /// two writers raced and the loser should re-read and retry - unlike the random
        /// short-link id, where a 409 is a collision to regenerate around.
        /// </summary>
        Task AddAsync(EmailSuppression suppression);

        /// <summary>
        /// Replaces the record, requiring the ETag it was read with. Throws
        /// <see cref="ConcurrencyConflictException"/> on a lost race.
        /// </summary>
        Task UpdateAsync(EmailSuppression suppression);

        /// <summary>Returns the record for the address (active or cleared), or null.</summary>
        Task<EmailSuppression?> GetByEmailAsync(string email);

        /// <summary>Returns the record with the given document id, or null.</summary>
        Task<EmailSuppression?> GetByIdAsync(string id);

        /// <summary>Returns every record currently in force (<c>ClearedUtc</c> null).</summary>
        Task<List<EmailSuppression>> GetActiveAsync();

        /// <summary>Returns every record, cleared ones included.</summary>
        Task<List<EmailSuppression>> GetAllAsync();
    }

    public class CosmosEmailSuppressionRepository : IEmailSuppressionRepository
    {
        private readonly CosmosContainer _container;

        public CosmosEmailSuppressionRepository(CosmosContainer container)
        {
            _container = container;
        }

        public async Task AddAsync(EmailSuppression suppression)
        {
            if (string.IsNullOrWhiteSpace(suppression.Id))
                throw new ArgumentException("EmailSuppression.Id must be set.", nameof(suppression));

            try
            {
                var response = await _container.CreateItemAsync(ToDocument(suppression), CosmosPartitionKey.None);
                suppression.ETag = response.Headers.ETag;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
            {
                // The id is MakeId(address), so "already exists" means another writer recorded the
                // same address first - a bounce webhook racing a one-click, say. That is a lost
                // race, not a collision: the right response is re-read and retry, which is exactly
                // what ConcurrencyConflictException tells the service layer to do.
                throw new ConcurrencyConflictException("A suppression for this address already exists.", ex);
            }
        }

        public async Task UpdateAsync(EmailSuppression suppression)
        {
            if (string.IsNullOrWhiteSpace(suppression.Id))
                throw new ArgumentException("EmailSuppression.Id must be set.", nameof(suppression));
            if (string.IsNullOrWhiteSpace(suppression.ETag))
                throw new ArgumentException(
                    "EmailSuppression.ETag must be set for an update; read the record first.",
                    nameof(suppression)
                );

            try
            {
                var response = await _container.ReplaceItemAsync(
                    ToDocument(suppression),
                    suppression.Id,
                    CosmosPartitionKey.None,
                    new ItemRequestOptions { IfMatchEtag = suppression.ETag }
                );
                suppression.ETag = response.Headers.ETag;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                throw new ConcurrencyConflictException(
                    "The suppression was modified by another writer; re-read and retry.",
                    ex
                );
            }
        }

        public Task<EmailSuppression?> GetByEmailAsync(string email)
        {
            // A blank address can never have a record - MakeId would hash the empty string to a
            // perfectly valid id, so this is a correctness guard, not just politeness.
            if (string.IsNullOrWhiteSpace(email))
                return Task.FromResult<EmailSuppression?>(null);

            return GetByIdAsync(EmailSuppression.MakeId(email));
        }

        public async Task<EmailSuppression?> GetByIdAsync(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return null;

            try
            {
                var response = await _container.ReadItemAsync<JObject>(id, CosmosPartitionKey.None);
                var suppression = ToSuppression(response.Resource);
                suppression.ETag = response.Headers.ETag;
                return suppression;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound && ex.SubStatusCode == 0)
            {
                // Only a genuinely missing item maps to "not suppressed". A 404 with a non-zero
                // sub-status means the container or database is missing, and swallowing that would
                // let every send run unfiltered against a provisioning mistake - the enforcement
                // point deliberately fails the job instead.
                return null;
            }
        }

        public Task<List<EmailSuppression>> GetActiveAsync()
        {
            // ClearedUtc is written as an explicit JSON null (see ToDocument), so IS_NULL matches
            // in-force records; IS_DEFINED guards against any hand-authored document missing the
            // field entirely.
            return QueryAsync(
                "SELECT * FROM c WHERE c.Discriminator = 'EmailSuppression' AND (NOT IS_DEFINED(c.ClearedUtc) OR IS_NULL(c.ClearedUtc))"
            );
        }

        public Task<List<EmailSuppression>> GetAllAsync()
        {
            return QueryAsync("SELECT * FROM c WHERE c.Discriminator = 'EmailSuppression'");
        }

        private async Task<List<EmailSuppression>> QueryAsync(string sql)
        {
            var iterator = _container.GetItemQueryIterator<JObject>(new QueryDefinition(sql));
            var results = new List<EmailSuppression>();
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                foreach (var doc in response)
                {
                    // ETag set in the mapper from _etag so every query path carries it, per the
                    // repository conventions - a record read from a list must be updatable without
                    // a second read.
                    results.Add(ToSuppression(doc));
                }
            }

            return results;
        }

        /// <summary>
        /// Internal so a test can round-trip a document through real JSON text - the Mock stores
        /// live objects and never serialises, so it cannot observe a mapping that only breaks once
        /// Cosmos has written and re-read the document.
        /// </summary>
        internal static EmailSuppression ToSuppression(JObject doc)
        {
            // Guid parsed from the string form - Value<Guid?> routes a String token through
            // Convert.ChangeType and throws; see the comment on CosmosUnsubscribeLinkRepository.
            Guid? causingJobId = null;
            if (Guid.TryParse(doc.Value<string>("CausingJobId"), out var jobId))
                causingJobId = jobId;

            Enum.TryParse<SuppressionReason>(doc.Value<string>("Reason"), out var reason);

            return new EmailSuppression
            {
                Id = doc.Value<string>("id") ?? string.Empty,
                Email = doc.Value<string>("Email") ?? string.Empty,
                Reason = reason,
                ConsecutiveFailureCount = doc.Value<int?>("ConsecutiveFailureCount") ?? 0,
                FirstSeenUtc = doc["FirstSeenUtc"]?.ToObject<DateTime>() ?? DateTime.MinValue,
                LastSeenUtc = doc["LastSeenUtc"]?.ToObject<DateTime>() ?? DateTime.MinValue,
                CausingJobId = causingJobId,
                SuppressedUtc = doc["SuppressedUtc"]?.ToObject<DateTime>() ?? DateTime.MinValue,
                SuppressedBy = doc.Value<string>("SuppressedBy") ?? string.Empty,
                ProviderDiagnostic = doc.Value<string>("ProviderDiagnostic"),
                ClearedUtc =
                    doc["ClearedUtc"] is { Type: not JTokenType.Null } cleared ? cleared.ToObject<DateTime>() : null,
                ClearedBy = doc.Value<string>("ClearedBy"),
                ETag = doc.Value<string>("_etag"),
            };
        }

        /// <summary>Internal for the round-trip test described on <see cref="ToSuppression"/>.</summary>
        internal static JObject ToDocument(EmailSuppression suppression)
        {
            return new JObject
            {
                ["id"] = suppression.Id,
                ["Discriminator"] = "EmailSuppression",
                ["Email"] = suppression.Email,
                ["Reason"] = suppression.Reason.ToString(),
                ["ConsecutiveFailureCount"] = suppression.ConsecutiveFailureCount,
                ["FirstSeenUtc"] = JToken.FromObject(suppression.FirstSeenUtc),
                ["LastSeenUtc"] = JToken.FromObject(suppression.LastSeenUtc),
                ["CausingJobId"] = suppression.CausingJobId?.ToString("D"),
                ["SuppressedUtc"] = JToken.FromObject(suppression.SuppressedUtc),
                ["SuppressedBy"] = suppression.SuppressedBy,
                ["ProviderDiagnostic"] = suppression.ProviderDiagnostic,
                // Explicit null while active, so the GetActiveAsync IS_NULL branch matches a
                // written record rather than depending on the field's absence.
                ["ClearedUtc"] = suppression.ClearedUtc is { } clearedUtc ? JToken.FromObject(clearedUtc) : JValue.CreateNull(),
                ["ClearedBy"] = suppression.ClearedBy,
                // No ttl, deliberately: a suppression is permanent until a human clears it. A row
                // that quietly expired would resume mail to an address that bounced or complained.
            };
        }
    }
}
