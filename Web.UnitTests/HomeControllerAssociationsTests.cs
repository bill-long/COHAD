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
            new CurrentUserAccessor(users),
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
                users,
                Mock.Of<IAuditLogRepository>(),
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

    [Fact]
    public async Task RemoveAssociatedUser_propagates_conflict_for_the_409_filter_without_auditing()
    {
        var homeId = Guid.NewGuid();
        var requesterUniqueId = ExpectedUniqueId("u1", "google.com");
        var target = new User
        {
            UniqueId = "target-user",
            Emails = "target@example.com",
            OwnedHomeIds = new List<Guid> { homeId },
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
        mockUsers
            .Setup(r => r.UpsertAsync(It.IsAny<User>()))
            .ThrowsAsync(
                ConcurrencyConflictException.For("User", "target-user", new InvalidOperationException("ETag mismatch"))
            );

        var mockAudit = new Mock<IAuditLogRepository>();

        var c = CreateController(mockUsers.Object, Mock.Of<IHomeRepository>(), mockAudit.Object, nameId: "u1");

        // The exception must escape the action (the global ConcurrencyConflictExceptionFilter maps
        // it to the 409 refresh guidance) - and write-then-audit means no audit entry may describe
        // the change that never happened.
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => c.RemoveAssociatedUser(homeId, "target-user"));
        mockAudit.Verify(r => r.AddAsync(It.IsAny<NewAuditLogEntry>()), Times.Never);
    }

    [Fact]
    public async Task RemoveAssociatedUser_clears_resident_link_when_linked_residents_home_removed()
    {
        var homeId = Guid.NewGuid();
        var otherHomeId = Guid.NewGuid();
        var residentId = Guid.NewGuid();
        var requesterUniqueId = ExpectedUniqueId("u1", "google.com");
        var target = new User
        {
            UniqueId = "target-user",
            Emails = "target@example.com",
            OwnedHomeIds = new List<Guid> { homeId, otherHomeId },
            Roles = new List<User.Role>(),
            ResidentId = residentId,
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

        var mockResidents = new Mock<IResidentRepository>();
        mockResidents
            .Setup(r => r.GetByIdAsync(residentId))
            .ReturnsAsync(new Resident { Id = residentId, HomeId = homeId });

        var mockAudit = new Mock<IAuditLogRepository>();
        mockAudit.Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var c = CreateController(mockUsers.Object, Mock.Of<IHomeRepository>(), mockAudit.Object, mockResidents.Object, nameId: "u1");
        var result = await c.RemoveAssociatedUser(homeId, "target-user");

        Assert.IsType<OkResult>(result);
        Assert.Null(target.ResidentId);
    }

    [Fact]
    public async Task RemoveAssociatedUser_keeps_resident_link_when_other_home_removed()
    {
        var homeId = Guid.NewGuid();
        var linkedHomeId = Guid.NewGuid();
        var residentId = Guid.NewGuid();
        var requesterUniqueId = ExpectedUniqueId("u1", "google.com");
        var target = new User
        {
            UniqueId = "target-user",
            Emails = "target@example.com",
            OwnedHomeIds = new List<Guid> { homeId, linkedHomeId },
            Roles = new List<User.Role>(),
            ResidentId = residentId,
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

        var mockResidents = new Mock<IResidentRepository>();
        mockResidents
            .Setup(r => r.GetByIdAsync(residentId))
            .ReturnsAsync(new Resident { Id = residentId, HomeId = linkedHomeId });

        var mockAudit = new Mock<IAuditLogRepository>();
        mockAudit.Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var c = CreateController(mockUsers.Object, Mock.Of<IHomeRepository>(), mockAudit.Object, mockResidents.Object, nameId: "u1");
        var result = await c.RemoveAssociatedUser(homeId, "target-user");

        Assert.IsType<OkResult>(result);
        Assert.Equal(residentId, target.ResidentId);
    }

    [Fact]
    public async Task RemoveAssociatedUser_succeeds_and_keeps_link_when_resident_read_fails()
    {
        // The link check is hygiene only (readers treat an out-of-home link as no link), so a
        // transient resident-read failure must not fail the unassignment itself.
        var homeId = Guid.NewGuid();
        var residentId = Guid.NewGuid();
        var requesterUniqueId = ExpectedUniqueId("u1", "google.com");
        var target = new User
        {
            UniqueId = "target-user",
            Emails = "target@example.com",
            OwnedHomeIds = new List<Guid> { homeId },
            Roles = new List<User.Role>(),
            ResidentId = residentId,
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

        var mockResidents = new Mock<IResidentRepository>();
        mockResidents
            .Setup(r => r.GetByIdAsync(residentId))
            .ThrowsAsync(new InvalidOperationException("Cosmos error"));

        var mockAudit = new Mock<IAuditLogRepository>();
        mockAudit.Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var c = CreateController(mockUsers.Object, Mock.Of<IHomeRepository>(), mockAudit.Object, mockResidents.Object, nameId: "u1");
        var result = await c.RemoveAssociatedUser(homeId, "target-user");

        Assert.IsType<OkResult>(result);
        Assert.DoesNotContain(homeId, target.OwnedHomeIds);
        Assert.Equal(residentId, target.ResidentId);
        mockUsers.Verify(r => r.UpsertAsync(It.Is<User>(u => u.UniqueId == "target-user")), Times.Once);
    }
}
