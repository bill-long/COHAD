using System;
using System.Collections.Generic;
using Web.Models;
using Web.Services;
using Xunit;

namespace Web.UnitTests;

/// <summary>
/// Locks the single link-validity invariant: a usable link points at an existing, non-child
/// resident living in one of the user's owned homes. Every write path and reader shares this
/// predicate, so this is the one test for the rule.
/// </summary>
public sealed class ResidentLinkRulesTests
{
    private static readonly Guid HomeId = Guid.NewGuid();

    private static Resident Adult(Guid homeId) =>
        new() { Id = Guid.NewGuid(), HomeId = homeId, ResidentType = Resident.Type.Homeowner };

    [Fact]
    public void Usable_only_for_existing_adult_in_owned_home()
    {
        var ownedHomes = new List<Guid> { HomeId };

        Assert.True(ResidentLinkRules.IsUsable(Adult(HomeId), ownedHomes));

        var otherAdult = Adult(HomeId);
        otherAdult.ResidentType = Resident.Type.OtherAdult;
        Assert.True(ResidentLinkRules.IsUsable(otherAdult, ownedHomes));

        Assert.False(ResidentLinkRules.IsUsable(null, ownedHomes));
        Assert.False(ResidentLinkRules.IsUsable(Adult(Guid.NewGuid()), ownedHomes));
        Assert.False(ResidentLinkRules.IsUsable(Adult(HomeId), new List<Guid>()));
        Assert.False(ResidentLinkRules.IsUsable(Adult(HomeId), null));

        var child = Adult(HomeId);
        child.ResidentType = Resident.Type.Child;
        Assert.False(ResidentLinkRules.IsUsable(child, ownedHomes));

        // A legacy zero-id record can never be a link target: Guid.Empty is the clear sentinel.
        var zeroId = Adult(HomeId);
        zeroId.Id = Guid.Empty;
        Assert.False(ResidentLinkRules.IsUsable(zeroId, ownedHomes));
    }
}
