using System;
using System.Collections.Generic;
using Web.Models;
using Web.Services;
using Xunit;

namespace Web.UnitTests;

/// <summary>
/// Locks the single self-removal invariant shared by both home-association write paths: a caller
/// may not drop a home they own from their own account, and nothing else is a self-removal.
/// </summary>
public sealed class HomeAssociationRulesTests
{
    private static readonly Guid HomeA = Guid.NewGuid();
    private static readonly Guid HomeB = Guid.NewGuid();

    private static User Caller(params Guid[] homes) => new User { UniqueId = "me", OwnedHomeIds = new List<Guid>(homes) };

    [Fact]
    public void Dropping_an_owned_home_from_own_account_is_a_self_removal()
    {
        Assert.True(HomeAssociationRules.RemovesCallersOwnHome(Caller(HomeA, HomeB), "me", new[] { HomeA }));
    }

    [Fact]
    public void Emptying_own_home_list_is_a_self_removal()
    {
        Assert.True(HomeAssociationRules.RemovesCallersOwnHome(Caller(HomeA), "me", Array.Empty<Guid>()));
        Assert.True(HomeAssociationRules.RemovesCallersOwnHome(Caller(HomeA), "me", null));
    }

    [Fact]
    public void Targeting_another_user_is_never_a_self_removal()
    {
        Assert.False(HomeAssociationRules.RemovesCallersOwnHome(Caller(HomeA), "someone-else", Array.Empty<Guid>()));
        Assert.False(HomeAssociationRules.RemovesCallersOwnHome(Caller(HomeA), null, Array.Empty<Guid>()));
    }

    [Fact]
    public void Keeping_or_adding_homes_on_own_account_is_allowed()
    {
        Assert.False(HomeAssociationRules.RemovesCallersOwnHome(Caller(HomeA), "me", new[] { HomeA }));
        Assert.False(HomeAssociationRules.RemovesCallersOwnHome(Caller(HomeA), "me", new[] { HomeA, HomeB }));
    }

    [Fact]
    public void A_caller_with_no_homes_cannot_self_remove()
    {
        Assert.False(HomeAssociationRules.RemovesCallersOwnHome(Caller(), "me", Array.Empty<Guid>()));
        Assert.False(HomeAssociationRules.RemovesCallersOwnHome(new User { UniqueId = "me", OwnedHomeIds = null }, "me", null));
    }
}
