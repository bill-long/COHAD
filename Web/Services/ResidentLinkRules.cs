#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Web.Models;

namespace Web.Services
{
    /// <summary>
    /// The single definition of when a user's resident link (<see cref="User.ResidentId"/>) is
    /// usable: the resident exists with a real id, lives in one of the user's owned homes, and is
    /// not a child. Legacy zero-id resident records are unusable because <see cref="Guid.Empty"/>
    /// is the link wire protocol's clear sentinel and can never round-trip as a link. Every site
    /// that validates, clears, or resolves through a link must use this predicate so the invariant
    /// cannot drift between the write paths and the readers.
    /// </summary>
    public static class ResidentLinkRules
    {
        public static bool IsUsable(Resident? resident, IReadOnlyCollection<Guid>? ownedHomeIds)
        {
            return resident != null
                && resident.Id != Guid.Empty
                && ownedHomeIds != null
                && ownedHomeIds.Contains(resident.HomeId)
                && resident.ResidentType != Resident.Type.Child;
        }
    }
}
