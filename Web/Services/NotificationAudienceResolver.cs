#nullable enable
using System.Collections.Generic;
using System.Linq;
using Web.Models;

namespace Web.Services
{
    /// <summary>
    /// Resolves the set of notification audience keys a user belongs to: the Administrators audience for
    /// an Administrator, plus the committee audience for each committee the user can moderate
    /// (<see cref="CommitteeAuthorization.CanManage"/>). Centralized so the notifications API (badge and
    /// list), the SignalR hub (group membership), and any other consumer agree on exactly who sees a
    /// notification — drift here would let the push channel and the authorized list disagree. Pure so it
    /// can be unit-tested without a live connection or repository.
    /// </summary>
    public static class NotificationAudienceResolver
    {
        public static IReadOnlyList<string> Resolve(User? user, IEnumerable<Committee>? committees)
        {
            var audiences = new List<string>();
            if (user?.Roles?.Contains(User.Role.Administrator) == true)
                audiences.Add(NotificationAudience.Administrators);

            if (committees != null)
            {
                foreach (var committee in committees)
                {
                    if (committee != null
                        && !string.IsNullOrEmpty(committee.Id)
                        && CommitteeAuthorization.CanManage(user, committee))
                    {
                        audiences.Add(NotificationAudience.Committee(committee.Id));
                    }
                }
            }

            return audiences;
        }
    }
}
