using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Web.Controllers;
using Web.Models;
using Web.Services;
using Web.Services.Repositories;
using Xunit;

namespace Web.UnitTests;

public sealed class HomeControllerAssociationsTests
{
    private const string IdentityProviderClaim = "http://schemas.microsoft.com/identity/claims/identityprovider";

    private static HomeController CreateController(
        IUserRepository users,
        IHomeRepository homes,
        IAuditLogRepository audit,
        IResidentRepository? residents = null,
        string nameId = "nid-1",
        string idp = "google.com"
    )
    {
        if (residents == null)
        {
            var mock = new Mock<IResidentRepository>();
            mock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Resident>());
            mock.Setup(r => r.GetByHomeIdAsync(It.IsAny<Guid>())).ReturnsAsync(new List<Resident>());
            residents = mock.Object;
        }
        var c = new HomeController(
            users,
            homes,
            residents,
            audit,
            new ResidentCleanupService(
                Mock.Of<ICommitteeRepository>(),
                new CommitteeListCache(
                    Mock.Of<ICommitteeRepository>(),
                    new Microsoft.Extensions.Caching.Memory.MemoryCache(
                        new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()
                    )
                ),
                Mock.Of<IDocumentFileStore>(),
                Mock.Of<ILogger<ResidentCleanupService>>()
            ),
            Mock.Of<ILogger<HomeController>>()
        )
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(
                        new ClaimsIdentity(
                            new[]
                            {
                                new Claim(ClaimTypes.NameIdentifier, nameId),
                                new Claim(IdentityProviderClaim, idp),
                            },
                            "Test"
                        )
                    ),
                },
            },
        };
        return c;
    }

    private static string ExpectedUniqueId(string nameId, string idp) => $"{idp}{nameId}";

    [Fact]
    public async Task Get_populates_associated_users_for_each_home()
    {
        var homeA = Guid.NewGuid();
        var homeB = Guid.NewGuid();
        var homes = new List<Home>
        {
            new Home
            {
                Id = homeA,
                StreetNumber = 1,
                StreetName = "A",
                Residents = new List<Resident>(),
            },
            new Home
            {
                Id = homeB,
                StreetNumber = 2,
                StreetName = "B",
                Residents = new List<Resident>(),
            },
        };
        var users = new List<User>
        {
            new User
            {
                UniqueId = "u1",
                GivenName = "U",
                Surname = "One",
                Emails = "u1@test.com",
                OwnedHomeIds = new List<Guid> { homeA },
            },
            new User
            {
                UniqueId = "u2",
                GivenName = "U",
                Surname = "Two",
                Emails = "u2@test.com",
                OwnedHomeIds = new List<Guid> { homeA, homeB },
            },
            new User
            {
                UniqueId = "u3",
                GivenName = "U",
                Surname = "Three",
                Emails = "u3@test.com",
                OwnedHomeIds = new List<Guid>(),
            },
        };

        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetAllAsync()).ReturnsAsync(users);
        var mockHomes = new Mock<IHomeRepository>();
        mockHomes.Setup(r => r.GetAllAsync()).ReturnsAsync(homes);

        var c = CreateController(mockUsers.Object, mockHomes.Object, Mock.Of<IAuditLogRepository>());

        var result = await c.Get();
        var list = new List<Home>(result);

        Assert.Equal(2, list.Count);
        Assert.Equal(2, list[0].AssociatedUsers.Count);
        Assert.Single(list[1].AssociatedUsers);
        Assert.Contains(list[0].AssociatedUsers, a => a.UniqueId == "u1");
        Assert.Contains(list[0].AssociatedUsers, a => a.UniqueId == "u2");
        Assert.Contains(list[1].AssociatedUsers, a => a.UniqueId == "u2");
    }

    [Fact]
    public async Task RemoveAssociatedUser_returns_Forbid_when_requester_not_owner_and_not_admin()
    {
        var homeId = Guid.NewGuid();
        var requesterUniqueId = ExpectedUniqueId("u1", "google.com");
        var mockUsers = new Mock<IUserRepository>();
        mockUsers
            .Setup(r => r.GetByUniqueIdAsync(requesterUniqueId))
            .ReturnsAsync(
                new User
                {
                    UniqueId = requesterUniqueId,
                    Roles = new List<User.Role> { User.Role.Resident },
                    OwnedHomeIds = new List<Guid>(),
                }
            );

        var c = CreateController(
            mockUsers.Object,
            Mock.Of<IHomeRepository>(),
            Mock.Of<IAuditLogRepository>(),
            nameId: "u1"
        );
        var result = await c.RemoveAssociatedUser(homeId, "target-user");
        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task RemoveAssociatedUser_removes_home_and_writes_audit_when_requester_is_owner()
    {
        var homeId = Guid.NewGuid();
        var requesterUniqueId = ExpectedUniqueId("u1", "google.com");
        var target = new User
        {
            UniqueId = "target-user",
            Emails = "target@example.com",
            OwnedHomeIds = new List<Guid> { homeId, Guid.NewGuid() },
            Roles = new List<User.Role>(),
        };
        var mockUsers = new Mock<IUserRepository>();
        mockUsers
            .Setup(r => r.GetByUniqueIdAsync(requesterUniqueId))
            .ReturnsAsync(
                new User
                {
                    UniqueId = requesterUniqueId,
                    Roles = new List<User.Role> { User.Role.Resident },
                    OwnedHomeIds = new List<Guid> { homeId },
                }
            );
        mockUsers.Setup(r => r.GetByUniqueIdAsync("target-user")).ReturnsAsync(target);
        mockUsers.Setup(r => r.UpsertAsync(It.IsAny<User>())).ReturnsAsync((User u) => u);

        var mockAudit = new Mock<IAuditLogRepository>();
        mockAudit.Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var c = CreateController(mockUsers.Object, Mock.Of<IHomeRepository>(), mockAudit.Object, nameId: "u1");
        var result = await c.RemoveAssociatedUser(homeId, "target-user");

        Assert.IsType<OkResult>(result);
        Assert.DoesNotContain(homeId, target.OwnedHomeIds);
        mockUsers.Verify(r => r.UpsertAsync(It.Is<User>(u => u.UniqueId == "target-user")), Times.Once);
        mockAudit.Verify(r => r.AddAsync(It.IsAny<NewAuditLogEntry>()), Times.Once);
    }
}
