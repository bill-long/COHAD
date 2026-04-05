using System;
using System.Collections.Generic;
using System.Linq;
using Web.Models;
using static Web.PresentationModels.CommitteeMemberHelpers;

namespace Web.PresentationModels
{
    /// <summary>Public view of a committee (excludes Graph sync and forwarding internals).</summary>
    public class CommitteeCard
    {
        public string Id { get; set; }

        public string DisplayName { get; set; }

        public string Description { get; set; }

        public string CommitteeEmail { get; set; }

        public int DisplayOrder { get; set; }

        public int MemberCount { get; set; }

        public List<CommitteeMemberCard> Members { get; set; }

        public static CommitteeCard FromStorageModel(Committee c, IReadOnlyDictionary<Guid, Resident> residents) =>
            new()
            {
                Id = c.Id,
                DisplayName = c.DisplayName,
                Description = c.Description,
                CommitteeEmail = c.CommitteeEmail,
                DisplayOrder = c.DisplayOrder,
                MemberCount = c.Members?.Count ?? 0,
                Members = (c.Members ?? new List<CommitteeMember>())
                    .OrderBy(m => m.DisplayOrder)
                    .Select(m =>
                        CommitteeMemberCard.FromStorageModel(m, c.Id, residents.GetValueOrDefault(m.ResidentId))
                    )
                    .ToList(),
            };
    }

    /// <summary>Public view of a committee member (excludes email, homeId, forwarding flag).</summary>
    public class CommitteeMemberCard
    {
        public Guid Id { get; set; }

        public string DisplayName { get; set; }

        public string Title { get; set; }

        public string Bio { get; set; }

        public bool HasPhoto { get; set; }

        public string PhotoDownloadUrl { get; set; }

        public int PhotoOffsetY { get; set; }

        public int DisplayOrder { get; set; }

        internal static CommitteeMemberCard FromStorageModel(
            CommitteeMember m,
            string committeeKey,
            Resident resident
        ) =>
            new()
            {
                Id = m.Id,
                DisplayName = ResidentDisplayName(resident),
                Title = m.Title,
                Bio = m.Bio,
                HasPhoto = !string.IsNullOrWhiteSpace(m.PhotoBlobPath),
                PhotoDownloadUrl = !string.IsNullOrWhiteSpace(m.PhotoBlobPath)
                    ? $"/api/committee/{Uri.EscapeDataString(committeeKey)}/members/{m.Id:D}/photo"
                    : null,
                PhotoOffsetY = m.PhotoOffsetY,
                DisplayOrder = m.DisplayOrder,
            };
    }

    /// <summary>Admin view of a committee (includes all fields).</summary>
    public class CommitteeAdmin
    {
        public string Id { get; set; }

        public string DisplayName { get; set; }

        public string Description { get; set; }

        public string CommitteeEmail { get; set; }

        public int DisplayOrder { get; set; }

        public List<CommitteeMemberAdmin> Members { get; set; }

        public DateTime? LastSyncedUtc { get; set; }

        public string LastSyncStatus { get; set; }

        public string LastSyncError { get; set; }

        public static CommitteeAdmin FromStorageModel(Committee c, IReadOnlyDictionary<Guid, Resident> residents) =>
            new()
            {
                Id = c.Id,
                DisplayName = c.DisplayName,
                Description = c.Description,
                CommitteeEmail = c.CommitteeEmail,
                DisplayOrder = c.DisplayOrder,
                Members = (c.Members ?? new List<CommitteeMember>())
                    .OrderBy(m => m.DisplayOrder)
                    .Select(m => CommitteeMemberAdmin.FromStorageModel(m, residents.GetValueOrDefault(m.ResidentId)))
                    .ToList(),
                LastSyncedUtc = c.LastSyncedUtc,
                LastSyncStatus = c.LastSyncStatus,
                LastSyncError = c.LastSyncError,
            };
    }

    /// <summary>Admin view of a committee member (includes email, residentId, forwarding).</summary>
    public class CommitteeMemberAdmin
    {
        public Guid Id { get; set; }

        public Guid ResidentId { get; set; }

        public string DisplayName { get; set; }

        public string Title { get; set; }

        public string Bio { get; set; }

        public bool HasPhoto { get; set; }

        public string Email { get; set; }

        public bool ReceivesForwardedEmail { get; set; }

        public int PhotoOffsetY { get; set; }

        public int DisplayOrder { get; set; }

        internal static CommitteeMemberAdmin FromStorageModel(CommitteeMember m, Resident resident) =>
            new()
            {
                Id = m.Id,
                ResidentId = m.ResidentId,
                DisplayName = ResidentDisplayName(resident),
                Title = m.Title,
                Bio = m.Bio,
                HasPhoto = !string.IsNullOrWhiteSpace(m.PhotoBlobPath),
                Email = resident?.EmailAddresses?.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e?.Address))?.Address,
                ReceivesForwardedEmail = m.ReceivesForwardedEmail,
                PhotoOffsetY = m.PhotoOffsetY,
                DisplayOrder = m.DisplayOrder,
            };
    }

    internal static class CommitteeMemberHelpers
    {
        internal static string ResidentDisplayName(Resident resident)
        {
            if (resident == null)
                return "Unknown";
            return $"{resident.GivenName} {resident.Surname}".Trim();
        }
    }
}
