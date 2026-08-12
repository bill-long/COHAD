using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Web.Models;
using Web.Services.Repositories;

namespace Web.Services
{
    /// <summary>
    /// Handles cascade cleanup when residents are removed - removes matching committee members
    /// (cleaning up their photo blobs) and clears any user accounts' links to the deleted residents.
    /// A single entry point so a call site cannot apply half the cascade.
    /// </summary>
    public sealed class ResidentCleanupService
    {
        private readonly ICommitteeRepository _committeeRepository;
        private readonly CommitteeListCache _listCache;
        private readonly IDocumentFileStore _documentFileStore;
        private readonly IUserRepository _userRepository;
        private readonly IAuditLogRepository _auditLogRepository;
        private readonly ILogger<ResidentCleanupService> _logger;

        public ResidentCleanupService(
            ICommitteeRepository committeeRepository,
            CommitteeListCache listCache,
            IDocumentFileStore documentFileStore,
            IUserRepository userRepository,
            IAuditLogRepository auditLogRepository,
            ILogger<ResidentCleanupService> logger
        )
        {
            _committeeRepository = committeeRepository;
            _listCache = listCache;
            _documentFileStore = documentFileStore;
            _userRepository = userRepository;
            _auditLogRepository = auditLogRepository;
            _logger = logger;
        }

        /// <summary>
        /// Applies the full deleted-resident cascade: removes committee members that reference any of
        /// the given resident IDs (deleting associated photo blobs and invalidating the committee list
        /// cache) and clears <see cref="User.ResidentId"/> links pointing at them. The two halves
        /// touch disjoint stores and run concurrently; each is best-effort per record - the resident
        /// deletions have already happened, and a link left dangling only degrades that user to the
        /// account-email fallback.
        /// </summary>
        public async Task HandleDeletedResidentsAsync(IReadOnlyCollection<Guid> removedResidentIds)
        {
            if (removedResidentIds == null || removedResidentIds.Count == 0)
                return;

            var removedSet = new HashSet<Guid>(removedResidentIds);
            await Task.WhenAll(RemoveFromCommitteesAsync(removedSet), ClearUserLinksAsync(removedSet));
        }

        private async Task RemoveFromCommitteesAsync(HashSet<Guid> removedSet)
        {
            var committees = await _committeeRepository.GetAllAsync();
            var anyModified = false;

            foreach (var committee in committees)
            {
                if (committee.Members == null || committee.Members.Count == 0)
                    continue;

                var toRemove = committee.Members.Where(m => removedSet.Contains(m.ResidentId)).ToList();

                if (toRemove.Count == 0)
                    continue;

                var blobsToDelete = new List<string>();

                foreach (var member in toRemove)
                {
                    if (!string.IsNullOrWhiteSpace(member.PhotoBlobPath))
                        blobsToDelete.Add(member.PhotoBlobPath);

                    committee.Members.Remove(member);
                    _logger.LogInformation(
                        "Removed committee member {MemberId} (ResidentId {ResidentId}) from {Committee} due to resident deletion",
                        member.Id,
                        member.ResidentId,
                        committee.Id
                    );
                }

                // Persist the committee change first, then delete blobs.
                try
                {
                    await _committeeRepository.UpsertAsync(committee);
                    anyModified = true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Failed to persist committee {CommitteeId} after removing members; skipping blob cleanup for this committee",
                        committee.Id
                    );
                    continue;
                }

                foreach (var blobPath in blobsToDelete)
                {
                    try
                    {
                        await _documentFileStore.DeleteAsync(blobPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Failed to delete photo blob {BlobPath} after committee member removal",
                            blobPath
                        );
                    }
                }
            }

            if (anyModified)
            {
                _listCache.Invalidate();
            }
        }

        private async Task ClearUserLinksAsync(HashSet<Guid> removedSet)
        {
            List<User> users;
            try
            {
                users = await _userRepository.GetAllAsync() ?? new List<User>();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(
                    ex,
                    "Failed to load users while clearing resident links; dangling links will fall back to account email"
                );
                return;
            }

            foreach (var candidate in users.Where(u => u.ResidentId != null && removedSet.Contains(u.ResidentId.Value)))
            {
                var cleared = false;
                try
                {
                    // Re-read just before writing: the list snapshot above may already be stale, and
                    // upserting the freshest document (with its ETag protecting the write) keeps
                    // this background write from reverting a concurrent role or home change.
                    var fresh = await _userRepository.GetByUniqueIdAsync(candidate.UniqueId);
                    if (fresh?.ResidentId == null || !removedSet.Contains(fresh.ResidentId.Value))
                        continue;

                    fresh.ResidentId = null;
                    await _userRepository.UpsertAsync(fresh);
                    cleared = true;
                }
                catch (ConcurrencyConflictException ex)
                {
                    // Lost the race between the fresh read and the write. Per-record best-effort,
                    // like every failure here: the dangling link only degrades that user to the
                    // account-email fallback, so a warning suffices - nothing needs to page.
                    _logger.LogWarning(
                        ex,
                        "Skipped clearing resident link {ResidentId} from user {UserId}: the record was modified concurrently; the dangling link falls back to account email",
                        candidate.ResidentId,
                        candidate.UniqueId
                    );
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(
                        ex,
                        "Failed to clear resident link {ResidentId} from user {UserId}; the dangling link falls back to account email",
                        candidate.ResidentId,
                        candidate.UniqueId
                    );
                }

                if (!cleared)
                    continue;

                // The audit entry is what makes this automatic mutation visible after the fact:
                // every other user-association change writes one, and production logging captures
                // Warning and above, so a log line alone would be dropped. Best-effort in its own
                // try - a failed audit after an applied clear must be reported as exactly that,
                // never as a failure to clear.
                try
                {
                    await _auditLogRepository.AddAsync(
                        new NewAuditLogEntry
                        {
                            Id = Guid.NewGuid(),
                            SubjectId = candidate.UniqueId,
                            SubjectName = candidate.Emails,
                            Action = "Cleared the resident link because the linked resident was deleted.",
                            Time = DateTime.UtcNow,
                            UserDisplayName = "System",
                            UserId = "system",
                        }
                    );
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(
                        ex,
                        "Failed to write the audit entry for an applied resident-link clear on user {UserId}",
                        candidate.UniqueId
                    );
                }

                _logger.LogInformation(
                    "Cleared resident link {ResidentId} from user {UserId} due to resident deletion",
                    candidate.ResidentId,
                    candidate.UniqueId
                );
            }
        }
    }
}
