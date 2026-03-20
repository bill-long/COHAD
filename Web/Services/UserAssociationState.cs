using System;
using Web.Models;

namespace Web.Services;

/// <summary>
/// Keeps purge eligibility clocks aligned with home ownership and role assignment.
/// </summary>
public static class UserAssociationState
{
    /// <summary>
    /// Mutates the user so that users with no owned homes or no roles accumulate purge clocks,
    /// while users with homes/roles clear the corresponding clocks.
    /// </summary>
    public static void Apply(User user)
    {
        if (user == null)
        {
            throw new ArgumentNullException(nameof(user));
        }

        var hasHomes = user.OwnedHomeIds != null && user.OwnedHomeIds.Count > 0;
        if (hasHomes)
        {
            user.UnassociatedSinceUtc = null;
        }
        else
        {
            if (user.UnassociatedSinceUtc == null)
            {
                user.UnassociatedSinceUtc = DateTime.UtcNow;
            }
        }

        var hasRoles = user.Roles != null && user.Roles.Count > 0;
        if (hasRoles)
        {
            user.NoRolesSinceUtc = null;
        }
        else
        {
            if (user.NoRolesSinceUtc == null)
            {
                user.NoRolesSinceUtc = DateTime.UtcNow;
            }
        }
    }
}
