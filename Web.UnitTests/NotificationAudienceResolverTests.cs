using System.Collections.Generic;
using Web.Models;
using Web.Services;
using Xunit;

namespace Web.UnitTests;

public sealed class NotificationAudienceResolverTests
{
    private static readonly List<Committee> SampleCommittees = new()
    {
        new Committee { Id = "board", ManagementRole = User.Role.Board },
        new Committee { Id = "social", ManagementRole = User.Role.SocialCommittee },
    };

    [Fact]
    public void Administrator_gets_administrators_audience_and_every_committee()
    {
        var admin = new User { Roles = new List<User.Role> { User.Role.Administrator } };

        var audiences = NotificationAudienceResolver.Resolve(admin, SampleCommittees);

        Assert.Equal(
            new[]
            {
                NotificationAudience.Administrators,
                NotificationAudience.Committee("board"),
                NotificationAudience.Committee("social"),
            },
            audiences);
    }

    [Fact]
    public void Committee_role_holder_gets_only_their_committee_and_no_administrators_audience()
    {
        var boardMember = new User { Roles = new List<User.Role> { User.Role.Board } };

        var audiences = NotificationAudienceResolver.Resolve(boardMember, SampleCommittees);

        Assert.Equal(new[] { NotificationAudience.Committee("board") }, audiences);
        Assert.DoesNotContain(NotificationAudience.Administrators, audiences);
    }

    [Fact]
    public void Resident_with_no_management_role_gets_no_audiences()
    {
        var resident = new User { Roles = new List<User.Role> { User.Role.Resident } };

        Assert.Empty(NotificationAudienceResolver.Resolve(resident, SampleCommittees));
    }

    [Fact]
    public void Null_or_roleless_user_yields_no_audiences()
    {
        Assert.Empty(NotificationAudienceResolver.Resolve(null, SampleCommittees));
        Assert.Empty(NotificationAudienceResolver.Resolve(new User { Roles = null }, SampleCommittees));
    }

    [Fact]
    public void Administrator_with_null_committees_still_gets_administrators_audience()
    {
        // An admin holds the Administrators audience independent of any committee list.
        var audiences = NotificationAudienceResolver.Resolve(
            new User { Roles = new List<User.Role> { User.Role.Administrator } },
            new List<Committee>());

        Assert.Equal(new[] { NotificationAudience.Administrators }, audiences);
    }
}
