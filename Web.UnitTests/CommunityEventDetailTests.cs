using System;
using System.Collections.Generic;
using System.Linq;
using Web.Models;
using Web.PresentationModels;
using Xunit;

namespace Web.UnitTests;

public class CommunityEventDetailTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Personal_signup_is_visible_regardless_of_home_associations(int homeCount)
    {
        var homes = homeCount < 0 ? null : Enumerable.Range(0, homeCount).Select(_ => Guid.NewGuid()).ToList();
        var personal = new EventSignup
        {
            UserUniqueId = "current-user",
            Adults = 2,
            Children = 1,
            AdultNames = new List<string> { "Parent", "Guest" },
            ChildNames = new List<string> { "Child" },
        };
        var communityEvent = new CommunityEvent
        {
            Signups = new List<EventSignup>
            {
                new() { UserUniqueId = "another-user", Adults = 9 },
                personal,
            },
        };
        if (homes?.Count > 0)
        {
            communityEvent.Signups.Add(new EventSignup { HomeId = homes[0], Adults = 3 });
        }

        var detail = CommunityEventDetail.FromStorageModel(communityEvent, false, homes, "current-user");

        Assert.NotNull(detail.MyUserSignup);
        Assert.Null(detail.MyUserSignup.HomeId);
        Assert.Equal(2, detail.MyUserSignup.Adults);
        Assert.Equal(1, detail.MyUserSignup.Children);
        Assert.Equal(personal.AdultNames, detail.MyUserSignup.AdultNames);
        Assert.Equal(personal.ChildNames, detail.MyUserSignup.ChildNames);
        Assert.Equal(homes?.Count > 0 ? 1 : 0, detail.MyHomeSignups.Count);
        Assert.Empty(detail.Signups);
        Assert.Equal(communityEvent.Signups.Count, detail.TotalSignups);
        Assert.Equal(communityEvent.Signups.Sum(s => s.Adults), detail.TotalAdults);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("different-user")]
    public void Personal_signup_is_not_exposed_to_other_or_anonymous_users(string currentUserId)
    {
        var communityEvent = new CommunityEvent
        {
            Signups = new List<EventSignup>
            {
                new() { UserUniqueId = "owner", Adults = 2 },
            },
        };

        var detail = CommunityEventDetail.FromStorageModel(
            communityEvent,
            false,
            new[] { Guid.NewGuid() },
            currentUserId
        );

        Assert.Null(detail.MyUserSignup);
        Assert.Empty(detail.MyHomeSignups);
        Assert.Empty(detail.Signups);
    }

    [Fact]
    public void Home_signup_is_not_also_presented_as_a_personal_signup()
    {
        var homeId = Guid.NewGuid();
        var communityEvent = new CommunityEvent
        {
            Signups = new List<EventSignup>
            {
                new()
                {
                    HomeId = homeId,
                    UserUniqueId = "current-user",
                    Adults = 2,
                },
            },
        };

        var detail = CommunityEventDetail.FromStorageModel(communityEvent, false, new[] { homeId }, "current-user");

        Assert.Null(detail.MyUserSignup);
        Assert.Single(detail.MyHomeSignups);
    }

    [Fact]
    public void Missing_signup_collection_has_no_personal_signup()
    {
        var detail = CommunityEventDetail.FromStorageModel(
            new CommunityEvent { Signups = null },
            false,
            new[] { Guid.NewGuid() },
            "current-user"
        );

        Assert.Null(detail.MyUserSignup);
        Assert.Empty(detail.MyHomeSignups);
    }
}
