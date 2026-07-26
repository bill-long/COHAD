#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Web.Models;
using Web.Services.Repositories;

namespace Web.Services
{
    /// <summary>
    /// Central entry point for raising and resolving unified in-app notifications. Trigger sites
    /// (registration, vendor flagging, held-email moderation) call <see cref="RaiseAsync"/>; the
    /// corresponding action handlers call <see cref="ResolveAsync"/> (or <see cref="AcknowledgeAsync"/>
    /// for notifications whose only resolution is an explicit dismiss). Each create/resolve persists
    /// the notification and signals the audience so connected clients refresh.
    /// </summary>
    public interface INotificationService
    {
        /// <summary>
        /// Creates a notification for the given target, or returns the existing one if it was already
        /// raised (idempotent on <paramref name="targetType"/> + <paramref name="targetId"/>).
        /// </summary>
        Task<Notification> RaiseAsync(
            NotificationType type,
            string audienceKey,
            string targetType,
            string targetId,
            string title,
            string summary,
            string? deepLink = null,
            CancellationToken ct = default
        );

        /// <summary>
        /// Marks the notification for the given target resolved. Returns the resolved notification, or
        /// null if no notification exists for the target. Idempotent: an already-resolved notification
        /// is returned unchanged.
        /// </summary>
        Task<Notification?> ResolveAsync(string targetType, string targetId, string? resolvedBy, CancellationToken ct = default);

        /// <summary>
        /// Marks a notification resolved by its id (used for explicit "acknowledge/dismiss" of
        /// notifications, such as registrations, that have no other resolving action). Returns the
        /// notification, or null if it does not exist.
        /// </summary>
        Task<Notification?> AcknowledgeAsync(Guid notificationId, string? acknowledgedBy, CancellationToken ct = default);

        /// <summary>
        /// Re-opens (un-resolves) a notification, clearing its resolution state — the undo for
        /// <see cref="AcknowledgeAsync"/>. Returns the notification, or null if it does not exist.
        /// Idempotent: an already-unresolved notification is returned unchanged. Escalation state
        /// (<see cref="Notification.EscalatedUtc"/>) is left intact so a re-opened notification that
        /// was already emailed about is not emailed about again.
        /// </summary>
        Task<Notification?> UnacknowledgeAsync(Guid notificationId, CancellationToken ct = default);

        Task<IReadOnlyList<Notification>> GetUnresolvedForAudienceAsync(string audienceKey, int limit = 50, CancellationToken ct = default);

        /// <summary>Returns the notification with the given id, or null if it does not exist.</summary>
        Task<Notification?> GetByIdAsync(Guid notificationId, CancellationToken ct = default);
    }

    public sealed class NotificationService : INotificationService
    {
        private readonly INotificationRepository _repository;
        private readonly INotificationRealtimeNotifier _notifier;
        private readonly ILogger<NotificationService> _logger;

        public NotificationService(
            INotificationRepository repository,
            INotificationRealtimeNotifier notifier,
            ILogger<NotificationService> logger
        )
        {
            _repository = repository;
            _notifier = notifier;
            _logger = logger;
        }

        public async Task<Notification> RaiseAsync(
            NotificationType type,
            string audienceKey,
            string targetType,
            string targetId,
            string title,
            string summary,
            string? deepLink = null,
            CancellationToken ct = default
        )
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(audienceKey))
                throw new ArgumentException("Audience key is required.", nameof(audienceKey));
            if (string.IsNullOrEmpty(targetType))
                throw new ArgumentException("Target type is required.", nameof(targetType));
            if (string.IsNullOrEmpty(targetId))
                throw new ArgumentException("Target id is required.", nameof(targetId));

            // Idempotent: a target is only ever represented by one notification.
            var existing = await _repository.GetByTargetAsync(targetType, targetId);
            if (existing != null)
                return existing;

            var notification = new Notification
            {
                Id = Notification.DeterministicId(targetType, targetId),
                Type = type,
                AudienceKey = audienceKey,
                TargetType = targetType,
                TargetId = targetId,
                Title = title ?? string.Empty,
                Summary = summary ?? string.Empty,
                DeepLink = deepLink,
                CreatedUtc = DateTime.UtcNow,
            };

            try
            {
                await _repository.AddAsync(notification);
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
            {
                // Another caller created it concurrently — return the winner rather than duplicating.
                // Re-read by the deterministic id (a point read) rather than re-querying by target:
                // it's cheaper and doesn't depend on the secondary index being immediately queryable.
                var winner = await _repository.GetByIdAsync(notification.Id);
                if (winner != null)
                    return winner;
                throw;
            }

            await SignalAsync(audienceKey, ct);
            return notification;
        }

        public async Task<Notification?> ResolveAsync(string targetType, string targetId, string? resolvedBy, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var existing = await _repository.GetByTargetAsync(targetType, targetId);
            if (existing == null)
                return null;

            return await ResolveInternalAsync(existing, resolvedBy, ct);
        }

        public async Task<Notification?> AcknowledgeAsync(Guid notificationId, string? acknowledgedBy, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var existing = await _repository.GetByIdAsync(notificationId);
            if (existing == null)
                return null;

            return await ResolveInternalAsync(existing, acknowledgedBy, ct);
        }

        public async Task<Notification?> UnacknowledgeAsync(Guid notificationId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var existing = await _repository.GetByIdAsync(notificationId);
            if (existing == null)
                return null;

            if (existing.ResolvedUtc == null)
                return existing; // Already unresolved — idempotent no-op.

            existing.ResolvedUtc = null;
            existing.ResolvedBy = null;
            await _repository.UpsertAsync(existing);
            await SignalAsync(existing.AudienceKey, ct);
            return existing;
        }

        public async Task<IReadOnlyList<Notification>> GetUnresolvedForAudienceAsync(string audienceKey, int limit = 50, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return await _repository.GetUnresolvedByAudienceAsync(audienceKey, limit);
        }

        public async Task<Notification?> GetByIdAsync(Guid notificationId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return await _repository.GetByIdAsync(notificationId);
        }

        private async Task<Notification> ResolveInternalAsync(Notification notification, string? resolvedBy, CancellationToken ct)
        {
            if (notification.ResolvedUtc != null)
                return notification; // Already resolved — idempotent no-op.

            notification.ResolvedUtc = DateTime.UtcNow;
            notification.ResolvedBy = resolvedBy;
            await _repository.UpsertAsync(notification);
            await SignalAsync(notification.AudienceKey, ct);
            return notification;
        }

        private async Task SignalAsync(string audienceKey, CancellationToken ct)
        {
            try
            {
                await _notifier.NotifyAudienceChangedAsync(audienceKey, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Best-effort: a failed live signal must not fail the persisted change. Clients still
                // pick the change up on their next fetch / reconnect. Cancellation (e.g. shutdown of a
                // background sweep) is allowed to propagate rather than be logged as a send failure.
                _logger.LogWarning(ex, "Failed to send notification signal to audience {AudienceKey}", audienceKey);
            }
        }
    }
}
