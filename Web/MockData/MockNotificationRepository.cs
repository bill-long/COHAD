#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Web.Models;
using Web.Services.Repositories;

namespace Web.MockData
{
    /// <summary>In-memory <see cref="INotificationRepository"/> for the MockData environment and unit tests.</summary>
    public sealed class MockNotificationRepository : INotificationRepository
    {
        private readonly Dictionary<Guid, Notification> _items = new();
        private int _versionCounter;

        public Task AddAsync(Notification notification)
        {
            lock (_items)
            {
                if (_items.ContainsKey(notification.Id))
                {
                    // Mirror Cosmos CreateItemAsync, which fails with 409 on a duplicate id.
                    throw new CosmosException("Notification already exists.", HttpStatusCode.Conflict, 0, string.Empty, 0);
                }

                var clone = Clone(notification);
                clone.ETag = Interlocked.Increment(ref _versionCounter).ToString();
                _items[clone.Id] = clone;
                notification.ETag = clone.ETag;
            }

            return Task.CompletedTask;
        }

        public Task<Notification?> GetByIdAsync(Guid id)
        {
            lock (_items)
            {
                return Task.FromResult(_items.TryGetValue(id, out var found) ? Clone(found) : null);
            }
        }

        public Task<Notification?> GetByTargetAsync(string targetType, string targetId)
        {
            if (string.IsNullOrEmpty(targetType) || string.IsNullOrEmpty(targetId))
                return Task.FromResult<Notification?>(null);

            lock (_items)
            {
                var match = _items.Values.FirstOrDefault(n =>
                    string.Equals(n.TargetType, targetType, StringComparison.Ordinal)
                    && string.Equals(n.TargetId, targetId, StringComparison.Ordinal)
                );
                return Task.FromResult(match != null ? Clone(match) : null);
            }
        }

        public Task UpsertAsync(Notification notification)
        {
            lock (_items)
            {
                var clone = Clone(notification);
                clone.ETag = Interlocked.Increment(ref _versionCounter).ToString();
                _items[clone.Id] = clone;
                notification.ETag = clone.ETag;
            }

            return Task.CompletedTask;
        }

        public Task UpsertWithEtagAsync(Notification notification)
        {
            // Mirror the Cosmos impl: an ETag is required for a conditional write.
            if (string.IsNullOrEmpty(notification.ETag))
                throw new InvalidOperationException("UpsertWithEtagAsync requires a notification loaded with its ETag.");

            lock (_items)
            {
                // Mirror Cosmos If-Match: fail with 412 when the stored ETag has moved on.
                if (_items.TryGetValue(notification.Id, out var current)
                    && !string.Equals(current.ETag, notification.ETag, StringComparison.Ordinal))
                {
                    throw new CosmosException(
                        "ETag mismatch.", HttpStatusCode.PreconditionFailed, 0, string.Empty, 0);
                }

                var clone = Clone(notification);
                clone.ETag = Interlocked.Increment(ref _versionCounter).ToString();
                _items[clone.Id] = clone;
                notification.ETag = clone.ETag;
            }

            return Task.CompletedTask;
        }

        public Task<List<Notification>> GetUnresolvedByAudienceAsync(string audienceKey, int limit = 50)
        {
            var clampedLimit = Math.Clamp(limit, 1, 200);
            lock (_items)
            {
                var list = _items
                    .Values.Where(n =>
                        string.Equals(n.AudienceKey, audienceKey, StringComparison.Ordinal) && n.ResolvedUtc == null
                    )
                    .OrderByDescending(n => n.CreatedUtc)
                    .Take(clampedLimit)
                    .Select(Clone)
                    .ToList();
                return Task.FromResult(list);
            }
        }

        public Task<List<Notification>> GetUnescalatedByAudienceOldestFirstAsync(string audienceKey, int limit = 200)
        {
            var clampedLimit = Math.Clamp(limit, 1, 500);
            lock (_items)
            {
                var list = _items
                    .Values.Where(n =>
                        string.Equals(n.AudienceKey, audienceKey, StringComparison.Ordinal)
                        && n.ResolvedUtc == null
                        && n.EscalatedUtc == null
                    )
                    .OrderBy(n => n.CreatedUtc)
                    .Take(clampedLimit)
                    .Select(Clone)
                    .ToList();
                return Task.FromResult(list);
            }
        }

        private static Notification Clone(Notification n)
        {
            return new Notification
            {
                Id = n.Id,
                Type = n.Type,
                AudienceKey = n.AudienceKey,
                TargetType = n.TargetType,
                TargetId = n.TargetId,
                Title = n.Title,
                Summary = n.Summary,
                CreatedUtc = n.CreatedUtc,
                ResolvedUtc = n.ResolvedUtc,
                ResolvedBy = n.ResolvedBy,
                EscalatedUtc = n.EscalatedUtc,
                EscalationJobId = n.EscalationJobId,
                ETag = n.ETag,
            };
        }
    }
}
