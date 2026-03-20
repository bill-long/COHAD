using System;
using Web.Models;

namespace Web.Services;

/// <summary>
/// Keeps <see cref="User.UnassociatedSinceUtc"/> aligned with <see cref="User.OwnedHomeIds"/> for purge eligibility.
/// </summary>
public static class UserAssociationState
{
    /// <summary>
    /// Mutates the user so that users with no owned homes accumulate a purge clock, and users with homes clear it.
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
    }
}
