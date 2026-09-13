#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Web.Models;

namespace Web.Services
{
    /// <summary>
    /// The single definition of the one home-association write a user may not make: removing a
    /// home from their own account. A resident who did so would be locked out of their home with
    /// no way back in, so both write paths that edit <see cref="User.OwnedHomeIds"/> (the home
    /// editor's per-owner delete and the Users screen's whole-list replace) refuse it with the same
    /// message. Another owner or an administrator can still remove the association on the user's
    /// behalf. Adding homes to one's own account, and every role change, remain allowed.
    /// </summary>
    public static class HomeAssociationRules
    {
        /// <summary>Returned by both write paths when the caller asks to drop a home they own.</summary>
        public const string SelfRemovalMessage =
            "You cannot remove your own account's association with a home. Ask another owner or an administrator to do it.";

        /// <summary>
        /// True when <paramref name="targetUniqueId"/> is the caller and <paramref name="remainingHomeIds"/>
        /// (the home list the write would leave behind) drops at least one home the caller owns now.
        /// A write that targets someone else, or that only adds homes, is never a self-removal.
        /// </summary>
        public static bool RemovesCallersOwnHome(User caller, string? targetUniqueId, IEnumerable<Guid>? remainingHomeIds)
        {
            if (targetUniqueId == null || targetUniqueId != caller.UniqueId)
            {
                return false;
            }

            var remaining = new HashSet<Guid>(remainingHomeIds ?? Enumerable.Empty<Guid>());
            return (caller.OwnedHomeIds ?? new List<Guid>()).Any(h => !remaining.Contains(h));
        }
    }
}
