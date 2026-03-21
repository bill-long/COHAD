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
            Roles = new List<User.Role> { User.Role.Resident },
            UnassociatedSinceUtc = DateTime.UtcNow.AddDays(-5),
            NoRolesSinceUtc = DateTime.UtcNow.AddDays(-5)
        };

        UserAssociationState.Apply(user);

        Assert.Null(user.UnassociatedSinceUtc);
        Assert.Null(user.NoRolesSinceUtc);
    }

    [Fact]
    public void Apply_with_no_homes_sets_clock_once()
    {
        var user = new User { OwnedHomeIds = new List<Guid>(), Roles = new List<User.Role> { User.Role.Resident } };
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
            Roles = new List<User.Role> { User.Role.Resident },
            UnassociatedSinceUtc = frozen
        };

        UserAssociationState.Apply(user);

        Assert.Equal(frozen, user.UnassociatedSinceUtc);
    }

    [Fact]
    public void Apply_with_null_OwnedHomeIds_treats_as_unassociated()
    {
        var user = new User { OwnedHomeIds = null, Roles = new List<User.Role> { User.Role.Resident } };

        UserAssociationState.Apply(user);

        Assert.NotNull(user.UnassociatedSinceUtc);
    }

    [Fact]
    public void Apply_with_no_roles_sets_NoRolesSinceUtc_once()
    {
        var user = new User
        {
            OwnedHomeIds = new List<Guid> { Guid.NewGuid() },
            Roles = new List<User.Role>()
        };
        var before = DateTime.UtcNow;

        UserAssociationState.Apply(user);

        Assert.NotNull(user.NoRolesSinceUtc);
        Assert.True(user.NoRolesSinceUtc >= before.AddSeconds(-2));
        Assert.True(user.NoRolesSinceUtc <= DateTime.UtcNow.AddSeconds(2));
    }

    [Fact]
    public void Apply_with_no_roles_preserves_existing_NoRolesSinceUtc()
    {
        var frozen = new DateTime(2024, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        var user = new User
        {
            OwnedHomeIds = new List<Guid> { Guid.NewGuid() },
            Roles = new List<User.Role>(),
            NoRolesSinceUtc = frozen
        };

        UserAssociationState.Apply(user);

        Assert.Equal(frozen, user.NoRolesSinceUtc);
    }

    [Fact]
    public void Apply_with_roles_clears_NoRolesSinceUtc()
    {
        var user = new User
        {
            OwnedHomeIds = new List<Guid> { Guid.NewGuid() },
            Roles = new List<User.Role> { User.Role.Resident },
            NoRolesSinceUtc = DateTime.UtcNow.AddDays(-10)
        };

        UserAssociationState.Apply(user);

        Assert.Null(user.NoRolesSinceUtc);
    }

    [Fact]
    public void Apply_with_no_roles_clears_all_owned_homes()
    {
        var user = new User
        {
            OwnedHomeIds = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() },
            Roles = new List<User.Role>()
        };

        UserAssociationState.Apply(user);

        Assert.Empty(user.OwnedHomeIds);
    }

    [Fact]
    public void Apply_with_no_homes_removes_non_admin_roles()
    {
        var user = new User
        {
            OwnedHomeIds = new List<Guid>(),
            Roles = new List<User.Role> { User.Role.Resident, User.Role.Board, User.Role.SocialCommittee }
        };

        UserAssociationState.Apply(user);

        Assert.Empty(user.Roles);
    }

    [Fact]
    public void Apply_with_no_homes_keeps_administrator_role()
    {
        var user = new User
        {
            OwnedHomeIds = null,
            Roles = new List<User.Role> { User.Role.Resident, User.Role.Administrator, User.Role.GardenClub }
        };

        UserAssociationState.Apply(user);

        Assert.Single(user.Roles);
        Assert.Contains(User.Role.Administrator, user.Roles);
    }

    [Fact]
    public void Apply_with_homes_and_roles_leaves_assignments_unchanged()
    {
        var homeA = Guid.NewGuid();
        var homeB = Guid.NewGuid();
        var user = new User
        {
            OwnedHomeIds = new List<Guid> { homeA, homeB },
            Roles = new List<User.Role> { User.Role.Administrator, User.Role.Resident }
        };

        UserAssociationState.Apply(user);

        Assert.Equal(2, user.OwnedHomeIds.Count);
        Assert.Contains(homeA, user.OwnedHomeIds);
        Assert.Contains(homeB, user.OwnedHomeIds);
        Assert.Equal(2, user.Roles.Count);
        Assert.Contains(User.Role.Administrator, user.Roles);
        Assert.Contains(User.Role.Resident, user.Roles);
    }
}
