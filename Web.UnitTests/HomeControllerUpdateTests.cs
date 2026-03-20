using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Web.Controllers;
using Web.Models;
using Web.Services.Repositories;
using Web.UpdateModels;
using Xunit;

namespace Web.UnitTests;

public sealed class HomeControllerUpdateTests
{
    private const string IdentityProviderClaim = "http://schemas.microsoft.com/identity/claims/identityprovider";

    private static HomeController CreateController(
        IUserRepository users,
        IHomeRepository homes,
        IAuditLogRepository audit,
        string nameId = "nid-1",
        string idp = "google.com")
    {
        var c = new HomeController(users, homes, audit)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.NameIdentifier, nameId),
                        new Claim(IdentityProviderClaim, idp)
                    }, "Test"))
                }
            }
        };
        return c;
    }

    private static string ExpectedUniqueId(string nameId, string idp) => $"{idp}{nameId}";

    [Fact]
    public async Task Update_returns_Forbid_when_not_owner_and_not_administrator()
    {
        var homeId = Guid.NewGuid();
        var uniqueId = ExpectedUniqueId("u1", "google.com");
        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(new User
        {
            UniqueId = uniqueId,
            OwnedHomeIds = new List<Guid>(),
            Roles = new List<User.Role> { User.Role.Resident }
        });

        var c = CreateController(mockUsers.Object, Mock.Of<IHomeRepository>(), Mock.Of<IAuditLogRepository>(),
            nameId: "u1", idp: "google.com");

        var result = await c.Update(new UpdatedHome { Id = homeId, Residents = new List<Resident>() });
        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task Update_returns_Ok_when_owner_even_if_not_administrator()
    {
        var homeId = Guid.NewGuid();
        var uniqueId = ExpectedUniqueId("u1", "google.com");
        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(new User
        {
            GivenName = "A",
            Surname = "B",
            UniqueId = uniqueId,
            OwnedHomeIds = new List<Guid> { homeId },
            Roles = new List<User.Role> { User.Role.Resident }
        });

        var stored = new Home
        {
            Id = homeId,
            StreetNumber = 1,
            StreetName = "Main",
            Residents = new List<Resident>()
        };
        var mockHomes = new Mock<IHomeRepository>();
        mockHomes.Setup(r => r.GetByIdAsync(homeId)).ReturnsAsync(stored);
        mockHomes.Setup(r => r.UpsertAsync(It.IsAny<Home>())).ReturnsAsync((Home h) => h);

        var mockAudit = new Mock<IAuditLogRepository>();
        mockAudit.Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var c = CreateController(mockUsers.Object, mockHomes.Object, mockAudit.Object, nameId: "u1");

        var result = await c.Update(new UpdatedHome { Id = homeId, Residents = new List<Resident>() });
        Assert.IsType<OkResult>(result);
        mockHomes.Verify(r => r.UpsertAsync(It.IsAny<Home>()), Times.Once);
        mockAudit.Verify(r => r.AddAsync(It.IsAny<NewAuditLogEntry>()), Times.Once);
    }

    [Fact]
    public async Task Update_returns_Ok_when_administrator_without_ownership()
    {
        var homeId = Guid.NewGuid();
        var uniqueId = ExpectedUniqueId("admin", "google.com");
        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(new User
        {
            GivenName = "Admin",
            Surname = "User",
            UniqueId = uniqueId,
            OwnedHomeIds = new List<Guid>(),
            Roles = new List<User.Role> { User.Role.Administrator }
        });

        var mockHomes = new Mock<IHomeRepository>();
        mockHomes.Setup(r => r.GetByIdAsync(homeId)).ReturnsAsync(new Home
        {
            Id = homeId,
            StreetNumber = 5,
            StreetName = "Oak",
            Residents = new List<Resident>()
        });
        mockHomes.Setup(r => r.UpsertAsync(It.IsAny<Home>())).ReturnsAsync((Home h) => h);

        var mockAudit = new Mock<IAuditLogRepository>();
        mockAudit.Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var c = CreateController(mockUsers.Object, mockHomes.Object, mockAudit.Object, nameId: "admin");

        var result = await c.Update(new UpdatedHome { Id = homeId, Residents = new List<Resident>() });
        Assert.IsType<OkResult>(result);
    }

    [Fact]
    public async Task Update_returns_NotFound_when_home_missing()
    {
        var homeId = Guid.NewGuid();
        var uniqueId = ExpectedUniqueId("u1", "google.com");
        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(new User
        {
            UniqueId = uniqueId,
            OwnedHomeIds = new List<Guid> { homeId },
            Roles = new List<User.Role> { User.Role.Resident }
        });

        var mockHomes = new Mock<IHomeRepository>();
        mockHomes.Setup(r => r.GetByIdAsync(homeId)).ReturnsAsync((Home?)null);

        var c = CreateController(mockUsers.Object, mockHomes.Object, Mock.Of<IAuditLogRepository>(), nameId: "u1");

        var result = await c.Update(new UpdatedHome { Id = homeId, Residents = new List<Resident>() });
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Update_strips_contact_info_for_child_residents()
    {
        var homeId = Guid.NewGuid();
        var uniqueId = ExpectedUniqueId("u1", "google.com");
        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(new User
        {
            UniqueId = uniqueId,
            OwnedHomeIds = new List<Guid> { homeId },
            Roles = new List<User.Role> { User.Role.Resident }
        });

        var mockHomes = new Mock<IHomeRepository>();
        mockHomes.Setup(r => r.GetByIdAsync(homeId)).ReturnsAsync(new Home
        {
            Id = homeId,
            StreetNumber = 1,
            StreetName = "S",
            Residents = new List<Resident>()
        });
        Home? saved = null;
        mockHomes.Setup(r => r.UpsertAsync(It.IsAny<Home>()))
            .Callback<Home>(h => saved = h)
            .ReturnsAsync((Home h) => h);

        var mockAudit = new Mock<IAuditLogRepository>();
        mockAudit.Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var c = CreateController(mockUsers.Object, mockHomes.Object, mockAudit.Object, nameId: "u1");

        await c.Update(new UpdatedHome
        {
            Id = homeId,
            Residents = new List<Resident>
            {
                new Resident
                {
                    GivenName = "Kid",
                    ResidentType = Resident.Type.Child,
                    EmailAddresses = new List<EmailAddress>
                    {
                        new EmailAddress { Address = "kid@test.com" }
                    },
                    PhoneNumbers = new List<PhoneNumber> { new PhoneNumber() }
                }
            }
        });

        Assert.NotNull(saved);
        Assert.Single(saved!.Residents);
        Assert.Empty(saved.Residents[0].EmailAddresses);
        Assert.Empty(saved.Residents[0].PhoneNumbers);
    }

    [Fact]
    public async Task Update_filters_invalid_email_addresses()
    {
        var homeId = Guid.NewGuid();
        var uniqueId = ExpectedUniqueId("u1", "google.com");
        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(new User
        {
            UniqueId = uniqueId,
            OwnedHomeIds = new List<Guid> { homeId },
            Roles = new List<User.Role> { User.Role.Resident }
        });

        var mockHomes = new Mock<IHomeRepository>();
        mockHomes.Setup(r => r.GetByIdAsync(homeId)).ReturnsAsync(new Home
        {
            Id = homeId,
            StreetNumber = 1,
            StreetName = "S",
            Residents = new List<Resident>()
        });
        Home? saved = null;
        mockHomes.Setup(r => r.UpsertAsync(It.IsAny<Home>()))
            .Callback<Home>(h => saved = h)
            .ReturnsAsync((Home h) => h);

        var mockAudit = new Mock<IAuditLogRepository>();
        mockAudit.Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var c = CreateController(mockUsers.Object, mockHomes.Object, mockAudit.Object, nameId: "u1");

        await c.Update(new UpdatedHome
        {
            Id = homeId,
            Residents = new List<Resident>
            {
                new Resident
                {
                    GivenName = "Adult",
                    ResidentType = Resident.Type.Homeowner,
                    EmailAddresses = new List<EmailAddress>
                    {
                        new EmailAddress { Address = "good@example.com" },
                        new EmailAddress { Address = "bad" },
                        new EmailAddress { Address = "" },
                        new EmailAddress { Address = "   " }
                    }
                }
            }
        });

        Assert.NotNull(saved);
        Assert.Single(saved!.Residents[0].EmailAddresses);
        Assert.Equal("good@example.com", saved.Residents[0].EmailAddresses[0].Address);
    }

    [Fact]
    public async Task Update_drops_residents_with_empty_GivenName()
    {
        var homeId = Guid.NewGuid();
        var uniqueId = ExpectedUniqueId("u1", "google.com");
        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(new User
        {
            UniqueId = uniqueId,
            OwnedHomeIds = new List<Guid> { homeId },
            Roles = new List<User.Role> { User.Role.Resident }
        });

        var mockHomes = new Mock<IHomeRepository>();
        mockHomes.Setup(r => r.GetByIdAsync(homeId)).ReturnsAsync(new Home
        {
            Id = homeId,
            StreetNumber = 1,
            StreetName = "S",
            Residents = new List<Resident>()
        });
        Home? saved = null;
        mockHomes.Setup(r => r.UpsertAsync(It.IsAny<Home>()))
            .Callback<Home>(h => saved = h)
            .ReturnsAsync((Home h) => h);

        var mockAudit = new Mock<IAuditLogRepository>();
        mockAudit.Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var c = CreateController(mockUsers.Object, mockHomes.Object, mockAudit.Object, nameId: "u1");

        await c.Update(new UpdatedHome
        {
            Id = homeId,
            Residents = new List<Resident>
            {
                new Resident { GivenName = "Keep", ResidentType = Resident.Type.Homeowner },
                new Resident { GivenName = "", ResidentType = Resident.Type.Homeowner },
                new Resident { GivenName = null!, ResidentType = Resident.Type.Homeowner }
            }
        });

        Assert.NotNull(saved);
        Assert.Single(saved!.Residents);
        Assert.Equal("Keep", saved.Residents[0].GivenName);
    }
}
