using System.Collections.Generic;
using System.Linq;
using Web.Hubs;
using Web.Models;
using Xunit;

namespace Web.UnitTests;

public sealed class HeldMessageNotificationsHubTests
{
    private static Committee Committee(string id, User.Role? managementRole) =>
        new Committee { Id = id, DisplayName = id, ManagementRole = managementRole };

    private static User UserWith(params User.Role[] roles) =>
        new User { UniqueId = "u1", Roles = roles.ToList() };

    private static readonly List<Committee> SampleCommittees = new()
    {
        Committee("board", User.Role.Board),
        Committee("social", User.Role.SocialCommittee),
        Committee("welcome", User.Role.WelcomeCommittee),
        Committee("unowned", null),
    };

    [Fact]
    public void CommitteeGroupName_is_stable_and_prefixed()
    {
        Assert.Equal("held:committee:board", HeldMessageNotificationsHub.CommitteeGroupName("board"));
    }

    [Fact]
    public void Administrator_manages_every_committee_including_unowned()
    {
        var ids = HeldMessageNotificationsHub.ResolveManagedCommitteeIds(
            UserWith(User.Role.Administrator),
            SampleCommittees
        );

        Assert.Equal(new[] { "board", "social", "unowned", "welcome" }, ids.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void Committee_role_holder_manages_only_matching_committee()
    {
        var ids = HeldMessageNotificationsHub.ResolveManagedCommitteeIds(
            UserWith(User.Role.Board),
            SampleCommittees
        );

        Assert.Equal(new[] { "board" }, ids.ToArray());
    }

    [Fact]
    public void Holder_of_multiple_roles_manages_each_matching_committee()
    {
        var ids = HeldMessageNotificationsHub.ResolveManagedCommitteeIds(
            UserWith(User.Role.Board, User.Role.WelcomeCommittee),
            SampleCommittees
        );

        Assert.Equal(new[] { "board", "welcome" }, ids.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void Resident_only_manages_nothing()
    {
        var ids = HeldMessageNotificationsHub.ResolveManagedCommitteeIds(
            UserWith(User.Role.Resident),
            SampleCommittees
        );

        Assert.Empty(ids);
    }

    [Fact]
    public void Null_user_or_null_roles_manages_nothing()
    {
        Assert.Empty(HeldMessageNotificationsHub.ResolveManagedCommitteeIds(null, SampleCommittees));
        Assert.Empty(HeldMessageNotificationsHub.ResolveManagedCommitteeIds(new User { Roles = null }, SampleCommittees));
    }
}
