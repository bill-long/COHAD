using System;
using System.Collections.Generic;
using Web.Models;
using Web.PresentationModels;
using Xunit;

namespace Web.UnitTests;

public sealed class PresentationUserTests
{
    [Fact]
    public void FromStorageModel_maps_fields_and_roles()
    {
        var user = new User
        {
            UniqueId = "google.comx",
            GivenName = "Pat",
            Surname = "Lee",
            StreetAddress = "1 A St",
            Emails = "pat@example.com",
            IdentityProvider = "google.com",
            LastLoggedIn = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            Roles = new List<User.Role> { User.Role.Resident, User.Role.Administrator },
            OwnedHomeIds = new List<Guid>()
        };
        var homes = new List<Home>
        {
            new Home { Id = Guid.NewGuid(), StreetNumber = 10, StreetName = "Oak" }
        };

        var p = PresentationUser.FromStorageModel(user, homes);

        Assert.Equal("google.comx", p.UniqueId);
        Assert.Equal("Pat", p.GivenName);
        Assert.Equal("Lee", p.Surname);
        Assert.Equal("Pat Lee", p.DisplayName);
        Assert.Equal("1 A St", p.StreetAddress);
        Assert.Equal("pat@example.com", p.Email);
        Assert.Equal("google.com", p.IdentityProvider);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), p.LastLoggedIn);
        Assert.Equal(new[] { nameof(User.Role.Resident), nameof(User.Role.Administrator) }, p.Roles);
        Assert.Single(p.OwnedHomes);
        Assert.Equal(10, p.OwnedHomes[0].StreetNumber);
    }
}
