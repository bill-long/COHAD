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
using Xunit;

namespace Web.UnitTests;

public sealed class UserControllerTests
{
    private const string IdentityProviderClaim = "http://schemas.microsoft.com/identity/claims/identityprovider";

    private static UserController CreateController(
        IUserRepository users,
        IHomeRepository homes,
        IAuditLogRepository audit,
        string nameId = "nid-1",
        string idp = "google.com")
    {
        var c = new UserController(users, homes, audit)
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

    private static string UniqueId(string nameId, string idp = "google.com") => $"{idp}{nameId}";

    // ── Issue 3: apiUser null-check tests ─────────────────────────────────────

    [Fact]
    public async Task AddOwnedHome_returns_NotFound_when_apiUser_not_in_database()
    {
        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(It.IsAny<string>())).ReturnsAsync((User?)null);
        var c = CreateController(mockUsers.Object, Mock.Of<IHomeRepository>(), Mock.Of<IAuditLogRepository>());

        var result = await c.AddOwnedHome("some-user-id", Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task RemoveOwnedHome_returns_NotFound_when_apiUser_not_in_database()
    {
        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(It.IsAny<string>())).ReturnsAsync((User?)null);
        var c = CreateController(mockUsers.Object, Mock.Of<IHomeRepository>(), Mock.Of<IAuditLogRepository>());

        var result = await c.RemoveOwnedHome("some-user-id", Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task AddUserRole_returns_NotFound_when_apiUser_not_in_database()
    {
        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(It.IsAny<string>())).ReturnsAsync((User?)null);
        var c = CreateController(mockUsers.Object, Mock.Of<IHomeRepository>(), Mock.Of<IAuditLogRepository>());

        var result = await c.AddUserRole("some-user-id", "Resident");

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task RemoveUserRole_returns_NotFound_when_apiUser_not_in_database()
    {
        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(It.IsAny<string>())).ReturnsAsync((User?)null);
        var c = CreateController(mockUsers.Object, Mock.Of<IHomeRepository>(), Mock.Of<IAuditLogRepository>());

        var result = await c.RemoveUserRole("some-user-id", "Resident");

        Assert.IsType<NotFoundResult>(result);
    }

    // ── Issue 2: null Roles list in AddUserRole / RemoveUserRole ─────────────

    [Fact]
    public async Task AddUserRole_succeeds_when_user_has_null_roles()
    {
        var apiUniqueId = UniqueId("admin");
        var targetUniqueId = "target-user";

        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(apiUniqueId)).ReturnsAsync(new User
        {
            UniqueId = apiUniqueId,
            GivenName = "Admin",
            Surname = "User",
            Roles = new List<User.Role> { User.Role.Administrator }
        });
        mockUsers.Setup(r => r.GetByUniqueIdAsync(targetUniqueId)).ReturnsAsync(new User
        {
            UniqueId = targetUniqueId,
            Roles = null   // null roles — this is the crash scenario
        });
        mockUsers.Setup(r => r.UpsertAsync(It.IsAny<User>())).ReturnsAsync((User u) => u);

        var mockAudit = new Mock<IAuditLogRepository>();
        mockAudit.Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var c = CreateController(mockUsers.Object, Mock.Of<IHomeRepository>(), mockAudit.Object, nameId: "admin");

        var result = await c.AddUserRole(targetUniqueId, "Resident");

        Assert.IsType<OkResult>(result);
        mockUsers.Verify(r => r.UpsertAsync(It.Is<User>(u => u.Roles != null && u.Roles.Contains(User.Role.Resident))), Times.Once);
    }

    [Fact]
    public async Task RemoveUserRole_does_not_crash_when_user_has_null_roles()
    {
        var apiUniqueId = UniqueId("admin");
        var targetUniqueId = "target-user";

        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(apiUniqueId)).ReturnsAsync(new User
        {
            UniqueId = apiUniqueId,
            GivenName = "Admin",
            Surname = "User",
            Roles = new List<User.Role> { User.Role.Administrator }
        });
        mockUsers.Setup(r => r.GetByUniqueIdAsync(targetUniqueId)).ReturnsAsync(new User
        {
            UniqueId = targetUniqueId,
            Roles = null   // null roles — this is the crash scenario
        });
        mockUsers.Setup(r => r.UpsertAsync(It.IsAny<User>())).ReturnsAsync((User u) => u);

        var mockAudit = new Mock<IAuditLogRepository>();
        mockAudit.Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var c = CreateController(mockUsers.Object, Mock.Of<IHomeRepository>(), mockAudit.Object, nameId: "admin");

        // Removing a role from a user who has no roles should be a no-op, not a crash
        var result = await c.RemoveUserRole(targetUniqueId, "Resident");

        Assert.IsType<OkResult>(result);
    }

    [Fact]
    public async Task AddUserRole_returns_Conflict_when_role_already_assigned()
    {
        var apiUniqueId = UniqueId("admin");
        var targetUniqueId = "target-user";

        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(apiUniqueId)).ReturnsAsync(new User
        {
            UniqueId = apiUniqueId,
            GivenName = "Admin",
            Surname = "User",
            Roles = new List<User.Role> { User.Role.Administrator }
        });
        mockUsers.Setup(r => r.GetByUniqueIdAsync(targetUniqueId)).ReturnsAsync(new User
        {
            UniqueId = targetUniqueId,
            Roles = new List<User.Role> { User.Role.Resident }  // role already present
        });

        var c = CreateController(mockUsers.Object, Mock.Of<IHomeRepository>(), Mock.Of<IAuditLogRepository>(), nameId: "admin");

        var result = await c.AddUserRole(targetUniqueId, "Resident");

        Assert.IsType<ConflictObjectResult>(result);
        mockUsers.Verify(r => r.UpsertAsync(It.IsAny<User>()), Times.Never);
    }
}
