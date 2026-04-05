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
    /// Handles cascade cleanup when residents are removed — removes matching
    /// committee members and cleans up their photo blobs.
    /// </summary>
    public sealed class ResidentCleanupService
    {
        private readonly ICommitteeRepository _committeeRepository;
        private readonly CommitteeListCache _listCache;
        private readonly IDocumentFileStore _documentFileStore;
        private readonly ILogger<ResidentCleanupService> _logger;

        public ResidentCleanupService(
            ICommitteeRepository committeeRepository,
            CommitteeListCache listCache,
            IDocumentFileStore documentFileStore,
            ILogger<ResidentCleanupService> logger)
        {
            _committeeRepository = committeeRepository;
            _listCache = listCache;
            _documentFileStore = documentFileStore;
            _logger = logger;
        }

        /// <summary>
        /// Removes committee members that reference any of the given resident IDs.
        /// Deletes associated photo blobs and invalidates the committee list cache.
        /// </summary>
        public async Task RemoveFromCommitteesAsync(IReadOnlyCollection<Guid> removedResidentIds)
        {
            if (removedResidentIds == null || removedResidentIds.Count == 0)
                return;

            var removedSet = new HashSet<Guid>(removedResidentIds);
            var committees = await _committeeRepository.GetAllAsync();
            var anyModified = false;

            foreach (var committee in committees)
            {
                if (committee.Members == null || committee.Members.Count == 0)
                    continue;

                var toRemove = committee.Members
                    .Where(m => removedSet.Contains(m.ResidentId))
                    .ToList();

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
                        member.Id, member.ResidentId, committee.Id);
                }

                // Persist the committee change first, then delete blobs.
                try
                {
                    await _committeeRepository.UpsertAsync(committee);
                    anyModified = true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Failed to persist committee {CommitteeId} after removing members; skipping blob cleanup for this committee",
                        committee.Id);
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
                        _logger.LogWarning(ex,
                            "Failed to delete photo blob {BlobPath} after committee member removal",
                            blobPath);
                    }
                }
            }

            if (anyModified)
            {
                _listCache.Invalidate();
            }
        }
    }
}
