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
    public interface INotificationRepository
    {
        /// <summary>Creates the notification. Throws <see cref="Microsoft.Azure.Cosmos.CosmosException"/> with status 409 if one with the same id already exists.</summary>
        Task AddAsync(Notification notification);

        Task<Notification?> GetByIdAsync(Guid id);

        /// <summary>Returns the notification for the given target, or null. Targets are unique per notification.</summary>
        Task<Notification?> GetByTargetAsync(string targetType, string targetId);

        /// <summary>Replaces an existing notification (used to set resolved/escalated state).</summary>
        Task UpsertAsync(Notification notification);

        /// <summary>Returns unresolved notifications for the audience, newest first.</summary>
        Task<List<Notification>> GetUnresolvedByAudienceAsync(string audienceKey, int limit = 50);
    }

    public class CosmosNotificationRepository : INotificationRepository
    {
        private readonly CosmosContainer _container;

        public CosmosNotificationRepository(CosmosContainer container)
        {
            _container = container;
        }

        public async Task AddAsync(Notification notification)
        {
            var response = await _container.CreateItemAsync(ToDocument(notification), CosmosPartitionKey.None);
            notification.ETag = response.Headers.ETag;
        }

        public async Task<Notification?> GetByIdAsync(Guid id)
        {
            // Point read rather than a query: cheaper, doesn't depend on the index, and reads the
            // just-created document reliably on the 409 recovery path (ids are deterministic per target).
            try
            {
                var response = await _container.ReadItemAsync<JObject>(ToDocumentId(id), CosmosPartitionKey.None);
                var notification = ToNotification(response.Resource);
                notification.ETag = response.Headers.ETag;
                return notification;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        public async Task<Notification?> GetByTargetAsync(string targetType, string targetId)
        {
            if (string.IsNullOrEmpty(targetType) || string.IsNullOrEmpty(targetId))
                return null;

            var query = new CosmosQueryDefinition(
                "SELECT TOP 1 * FROM c WHERE c.TargetType = @targetType AND c.TargetId = @targetId"
            )
                .WithParameter("@targetType", targetType)
                .WithParameter("@targetId", targetId);
            return await QuerySingleAsync(query);
        }

        public async Task UpsertAsync(Notification notification)
        {
            var response = await _container.UpsertItemAsync(ToDocument(notification), CosmosPartitionKey.None);
            notification.ETag = response.Headers.ETag;
        }

        public async Task<List<Notification>> GetUnresolvedByAudienceAsync(string audienceKey, int limit = 50)
        {
            var clampedLimit = Math.Clamp(limit, 1, 200);
            var query = new CosmosQueryDefinition(
                $"SELECT TOP {clampedLimit} * FROM c WHERE c.AudienceKey = @audienceKey "
                    + "AND (NOT IS_DEFINED(c.ResolvedUtc) OR IS_NULL(c.ResolvedUtc)) ORDER BY c.CreatedUtc DESC"
            ).WithParameter("@audienceKey", audienceKey);

            var iterator = _container.GetItemQueryIterator<JObject>(query);
            var results = new List<Notification>();
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                results.AddRange(response.Select(ToNotification));
            }

            return results;
        }

        private async Task<Notification?> QuerySingleAsync(CosmosQueryDefinition query)
        {
            var iterator = _container.GetItemQueryIterator<JObject>(query);
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                var doc = response.FirstOrDefault();
                if (doc != null)
                {
                    return ToNotification(doc);
                }
            }

            return null;
        }

        private static string ToDocumentId(Guid id) => $"Notification|{id:D}";

        private static Notification ToNotification(JObject doc)
        {
            var rawId = doc.Value<string>("id") ?? string.Empty;
            var parts = rawId.Split('|', StringSplitOptions.RemoveEmptyEntries);
            var parsedId = Guid.TryParse(parts.LastOrDefault(), out var id) ? id : Guid.Empty;

            return new Notification
            {
                Id = parsedId,
                Type = Enum.TryParse<NotificationType>(doc.Value<string>("Type"), out var type)
                    ? type
                    : NotificationType.Registration,
                AudienceKey = doc.Value<string>("AudienceKey") ?? string.Empty,
                TargetType = doc.Value<string>("TargetType") ?? string.Empty,
                TargetId = doc.Value<string>("TargetId") ?? string.Empty,
                Title = doc.Value<string>("Title") ?? string.Empty,
                Summary = doc.Value<string>("Summary") ?? string.Empty,
                CreatedUtc = doc["CreatedUtc"]?.ToObject<DateTime>() ?? DateTime.MinValue,
                ResolvedUtc = doc["ResolvedUtc"]?.ToObject<DateTime?>(),
                ResolvedBy = doc.Value<string>("ResolvedBy"),
                EscalatedUtc = doc["EscalatedUtc"]?.ToObject<DateTime?>(),
                EscalationJobId = Guid.TryParse(doc.Value<string>("EscalationJobId"), out var jobId)
                    ? jobId
                    : (Guid?)null,
                ETag = doc.Value<string>("_etag"),
            };
        }

        private static JObject ToDocument(Notification n)
        {
            return new JObject
            {
                ["id"] = ToDocumentId(n.Id),
                ["Discriminator"] = "Notification",
                ["Type"] = n.Type.ToString(),
                ["AudienceKey"] = n.AudienceKey,
                ["TargetType"] = n.TargetType,
                ["TargetId"] = n.TargetId,
                ["Title"] = n.Title ?? string.Empty,
                ["Summary"] = n.Summary ?? string.Empty,
                ["CreatedUtc"] = JToken.FromObject(n.CreatedUtc),
                ["ResolvedUtc"] = n.ResolvedUtc.HasValue ? JToken.FromObject(n.ResolvedUtc.Value) : JValue.CreateNull(),
                ["ResolvedBy"] = n.ResolvedBy,
                ["EscalatedUtc"] = n.EscalatedUtc.HasValue ? JToken.FromObject(n.EscalatedUtc.Value) : JValue.CreateNull(),
                ["EscalationJobId"] = n.EscalationJobId?.ToString("D"),
            };
        }
    }
}
