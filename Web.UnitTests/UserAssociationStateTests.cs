using System;
using System.Collections.Generic;
using Web.Models;
using Web.Services;
using Xunit;

namespace Web.UnitTests;

public sealed class UserAssociationStateTests
{
    [Fact]
    public void Apply_throws_when_user_null()
    {
        Assert.Throws<ArgumentNullException>(() => UserAssociationState.Apply(null!));
    }

    [Fact]
    public void Apply_with_owned_homes_clears_UnassociatedSinceUtc()
    {
        var user = new User
        {
            OwnedHomeIds = new List<Guid> { Guid.NewGuid() },
            UnassociatedSinceUtc = DateTime.UtcNow.AddDays(-5)
        };

        UserAssociationState.Apply(user);

        Assert.Null(user.UnassociatedSinceUtc);
    }

    [Fact]
    public void Apply_with_no_homes_sets_clock_once()
    {
        var user = new User { OwnedHomeIds = new List<Guid>() };
        var before = DateTime.UtcNow;

        UserAssociationState.Apply(user);

        Assert.NotNull(user.UnassociatedSinceUtc);
        Assert.True(user.UnassociatedSinceUtc >= before.AddSeconds(-2));
        Assert.True(user.UnassociatedSinceUtc <= DateTime.UtcNow.AddSeconds(2));
    }

    [Fact]
    public void Apply_with_no_homes_preserves_existing_clock()
    {
        var frozen = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var user = new User
        {
            OwnedHomeIds = new List<Guid>(),
            UnassociatedSinceUtc = frozen
        };

        UserAssociationState.Apply(user);

        Assert.Equal(frozen, user.UnassociatedSinceUtc);
    }

    [Fact]
    public void Apply_with_null_OwnedHomeIds_treats_as_unassociated()
    {
        var user = new User { OwnedHomeIds = null };

        UserAssociationState.Apply(user);

        Assert.NotNull(user.UnassociatedSinceUtc);
    }
}
