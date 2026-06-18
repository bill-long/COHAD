#nullable enable
namespace Web.Models
{
    /// <summary>
    /// Single source of truth for "may this user moderate this committee?". Used by both the
    /// REST endpoints (e.g. <c>CommitteeController.CanManageCommittee</c>) and the SignalR hub's
    /// per-committee group assignment, so the badge and the live push channel can't disagree.
    /// </summary>
    public static class CommitteeAuthorization
    {
        /// <summary>
        /// True if <paramref name="user"/> may manage <paramref name="committee"/>: an Administrator
        /// manages every committee; otherwise the committee's <see cref="Committee.ManagementRole"/>
        /// must be one of the user's roles.
        /// </summary>
        public static bool CanManage(User? user, Committee? committee)
        {
            if (user?.Roles == null || committee == null)
                return false;
            if (user.Roles.Contains(User.Role.Administrator))
                return true;
            return committee.ManagementRole.HasValue && user.Roles.Contains(committee.ManagementRole.Value);
        }
    }
}
