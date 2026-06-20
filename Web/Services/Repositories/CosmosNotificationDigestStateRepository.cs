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
    public interface INotificationDigestStateRepository
    {
        /// <summary>Returns the digest state for the recipient, or null if they have never been sent a digest.</summary>
        Task<NotificationDigestState?> GetAsync(string recipientEmail);

        /// <summary>Creates or replaces the recipient's digest state.</summary>
        Task UpsertAsync(NotificationDigestState state);
    }

    public class CosmosNotificationDigestStateRepository : INotificationDigestStateRepository
    {
        private readonly CosmosContainer _container;

        public CosmosNotificationDigestStateRepository(CosmosContainer container)
        {
            _container = container;
        }

        public async Task<NotificationDigestState?> GetAsync(string recipientEmail)
        {
            if (string.IsNullOrWhiteSpace(recipientEmail))
                return null;

            // Point read by the recipient's deterministic id — cheaper than a query and reliably reads
            // the just-written document on the next sweep.
            try
            {
                var docId = ToDocumentId(NotificationDigestState.DeterministicId(recipientEmail));
                var response = await _container.ReadItemAsync<JObject>(docId, CosmosPartitionKey.None);
                var state = ToState(response.Resource);
                state.ETag = response.Headers.ETag;
                return state;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        public async Task UpsertAsync(NotificationDigestState state)
        {
            // Guard empty/whitespace emails (mirrors GetAsync): an empty email hashes to a shared
            // deterministic id, so writing it would clobber an unrelated recipient's throttle state.
            if (string.IsNullOrWhiteSpace(state.RecipientEmail))
                return;

            var response = await _container.UpsertItemAsync(ToDocument(state), CosmosPartitionKey.None);
            state.ETag = response.Headers.ETag;
        }

        private static string ToDocumentId(string hashedEmail) => $"NotificationDigestState|{hashedEmail}";

        private static NotificationDigestState ToState(JObject doc)
        {
            return new NotificationDigestState
            {
                RecipientEmail = doc.Value<string>("RecipientEmail") ?? string.Empty,
                LastDigestUtc = doc["LastDigestUtc"]?.ToObject<DateTime>() ?? DateTime.MinValue,
                ETag = doc.Value<string>("_etag"),
            };
        }

        private static JObject ToDocument(NotificationDigestState s)
        {
            var normalized = (s.RecipientEmail ?? string.Empty).Trim().ToLowerInvariant();
            return new JObject
            {
                ["id"] = ToDocumentId(NotificationDigestState.DeterministicId(normalized)),
                ["Discriminator"] = "NotificationDigestState",
                ["RecipientEmail"] = normalized,
                ["LastDigestUtc"] = JToken.FromObject(s.LastDigestUtc),
            };
        }
    }
}
