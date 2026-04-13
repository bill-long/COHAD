using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Web.Controllers;
using Web.Models;
using Web.Services;
using Web.Services.Repositories;
using Web.UpdateModels;
using Xunit;

namespace Web.UnitTests;

public sealed class UserControllerTests
{
    private const string IdentityProviderClaim = "http://schemas.microsoft.com/identity/claims/identityprovider";

    private static UserController CreateController(
        IUserRepository users,
        IHomeRepository homes,
        IAuditLogRepository audit,
        IEventSignupConversionService signupConversion = null,
        string nameId = "nid-1",
        string idp = "google.com"
    )
    {
        var c = new UserController(users, homes, audit, signupConversion ?? Mock.Of<IEventSignupConversionService>())
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

    private static string UniqueId(string nameId, string idp = "google.com") => $"{idp}{nameId}";

    [Fact]
    public async Task UpdateUserAssociations_returns_NotFound_when_apiUser_not_in_database()
    {
        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(It.IsAny<string>())).ReturnsAsync((User?)null);
        var c = CreateController(mockUsers.Object, Mock.Of<IHomeRepository>(), Mock.Of<IAuditLogRepository>());

        var result = await c.UpdateUserAssociations("some-user-id", new UpdatedUserAssociations());

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task UpdateUserAssociations_updates_roles_and_homes_in_single_upsert()
    {
        var apiUniqueId = UniqueId("admin");
        var targetUniqueId = "target-user";
        var homeId = Guid.NewGuid();

        var mockUsers = new Mock<IUserRepository>();
        mockUsers
            .Setup(r => r.GetByUniqueIdAsync(apiUniqueId))
            .ReturnsAsync(
                new User
                {
                    UniqueId = apiUniqueId,
                    GivenName = "Admin",
                    Surname = "User",
                    Roles = new List<User.Role> { User.Role.Administrator },
                }
            );
        mockUsers
            .Setup(r => r.GetByUniqueIdAsync(targetUniqueId))
            .ReturnsAsync(
                new User
                {
                    UniqueId = targetUniqueId,
                    Emails = "target@example.com",
                    Roles = new List<User.Role>(),
                    OwnedHomeIds = new List<Guid>(),
                }
            );

        User? upserted = null;
        mockUsers
            .Setup(r => r.UpsertAsync(It.IsAny<User>()))
            .Callback<User>(u => upserted = u)
            .ReturnsAsync((User u) => u);

        var mockHomes = new Mock<IHomeRepository>();
        mockHomes
            .Setup(r => r.GetByIdsAsync(It.IsAny<List<Guid>>()))
            .ReturnsAsync(
                new List<Home>
                {
                    new Home
                    {
                        Id = homeId,
                        StreetNumber = 1,
                        StreetName = "Main",
                        Residents = new List<Resident>(),
                    },
                }
            );

        var mockAudit = new Mock<IAuditLogRepository>();
        mockAudit.Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var c = CreateController(mockUsers.Object, mockHomes.Object, mockAudit.Object, nameId: "admin");
        var result = await c.UpdateUserAssociations(
            targetUniqueId,
            new UpdatedUserAssociations
            {
                RoleNames = new List<string> { "Resident" },
                OwnedHomeIds = new List<Guid> { homeId },
            }
        );

        Assert.IsType<OkResult>(result);
        Assert.NotNull(upserted);
        Assert.Contains(User.Role.Resident, upserted!.Roles);
        Assert.Contains(homeId, upserted.OwnedHomeIds);
    }

    [Fact]
    public async Task UpdateUserAssociations_returns_BadRequest_for_unknown_role()
    {
        var apiUniqueId = UniqueId("admin");
        var targetUniqueId = "target-user";

        var mockUsers = new Mock<IUserRepository>();
        mockUsers
            .Setup(r => r.GetByUniqueIdAsync(apiUniqueId))
            .ReturnsAsync(
                new User
                {
                    UniqueId = apiUniqueId,
                    Roles = new List<User.Role> { User.Role.Administrator },
                }
            );
        mockUsers
            .Setup(r => r.GetByUniqueIdAsync(targetUniqueId))
            .ReturnsAsync(
                new User
                {
                    UniqueId = targetUniqueId,
                    Roles = new List<User.Role>(),
                    OwnedHomeIds = new List<Guid>(),
                }
            );

        var c = CreateController(
            mockUsers.Object,
            Mock.Of<IHomeRepository>(),
            Mock.Of<IAuditLogRepository>(),
            nameId: "admin"
        );
        var result = await c.UpdateUserAssociations(
            targetUniqueId,
            new UpdatedUserAssociations
            {
                RoleNames = new List<string> { "NotARole" },
                OwnedHomeIds = new List<Guid>(),
            }
        );

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task UpdateUserAssociations_converts_user_signups_when_home_assigned()
    {
        var apiUniqueId = UniqueId("admin");
        var targetUniqueId = "target-user";
        var homeId = Guid.NewGuid();

        var mockUsers = new Mock<IUserRepository>();
        mockUsers
            .Setup(r => r.GetByUniqueIdAsync(apiUniqueId))
            .ReturnsAsync(
                new User
                {
                    UniqueId = apiUniqueId,
                    GivenName = "Admin",
                    Surname = "User",
                    Roles = new List<User.Role> { User.Role.Administrator },
                }
            );
        mockUsers
            .Setup(r => r.GetByUniqueIdAsync(targetUniqueId))
            .ReturnsAsync(
                new User
                {
                    UniqueId = targetUniqueId,
                    Emails = "target@example.com",
                    Roles = new List<User.Role>(),
                    OwnedHomeIds = new List<Guid>(),
                }
            );
        mockUsers
            .Setup(r => r.UpsertAsync(It.IsAny<User>()))
            .ReturnsAsync((User u) => u);

        var mockHomes = new Mock<IHomeRepository>();
        mockHomes
            .Setup(r => r.GetByIdsAsync(It.IsAny<List<Guid>>()))
            .ReturnsAsync(
                new List<Home>
                {
                    new Home { Id = homeId, StreetNumber = 42, StreetName = "Oak Ave", Residents = new List<Resident>() },
                }
            );

        var mockAudit = new Mock<IAuditLogRepository>();
        mockAudit.Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var mockConversion = new Mock<IEventSignupConversionService>();
        mockConversion
            .Setup(s => s.ConvertUserSignupsToHomeAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>()))
            .ReturnsAsync(0);

        var c = CreateController(mockUsers.Object, mockHomes.Object, mockAudit.Object, mockConversion.Object, nameId: "admin");
        var result = await c.UpdateUserAssociations(
            targetUniqueId,
            new UpdatedUserAssociations
            {
                RoleNames = new List<string> { "Resident" },
                OwnedHomeIds = new List<Guid> { homeId },
            }
        );

        Assert.IsType<OkResult>(result);
        mockConversion.Verify(
            s => s.ConvertUserSignupsToHomeAsync(targetUniqueId, homeId, "42 Oak Ave"),
            Times.Once
        );
    }

    [Fact]
    public async Task UpdateUserAssociations_does_not_convert_signups_when_no_homes_assigned()
    {
        var apiUniqueId = UniqueId("admin");
        var targetUniqueId = "target-user";

        var mockUsers = new Mock<IUserRepository>();
        mockUsers
            .Setup(r => r.GetByUniqueIdAsync(apiUniqueId))
            .ReturnsAsync(
                new User
                {
                    UniqueId = apiUniqueId,
                    GivenName = "Admin",
                    Surname = "User",
                    Roles = new List<User.Role> { User.Role.Administrator },
                }
            );
        mockUsers
            .Setup(r => r.GetByUniqueIdAsync(targetUniqueId))
            .ReturnsAsync(
                new User
                {
                    UniqueId = targetUniqueId,
                    Emails = "target@example.com",
                    Roles = new List<User.Role>(),
                    OwnedHomeIds = new List<Guid>(),
                }
            );
        mockUsers
            .Setup(r => r.UpsertAsync(It.IsAny<User>()))
            .ReturnsAsync((User u) => u);

        var mockHomes = new Mock<IHomeRepository>();
        mockHomes
            .Setup(r => r.GetByIdsAsync(It.IsAny<List<Guid>>()))
            .ReturnsAsync(new List<Home>());

        var mockAudit = new Mock<IAuditLogRepository>();
        mockAudit.Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var mockConversion = new Mock<IEventSignupConversionService>();

        var c = CreateController(mockUsers.Object, mockHomes.Object, mockAudit.Object, mockConversion.Object, nameId: "admin");
        var result = await c.UpdateUserAssociations(
            targetUniqueId,
            new UpdatedUserAssociations
            {
                RoleNames = new List<string> { "Resident" },
                OwnedHomeIds = new List<Guid>(),
            }
        );

        Assert.IsType<OkResult>(result);
        mockConversion.Verify(
            s => s.ConvertUserSignupsToHomeAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>()),
            Times.Never
        );
    }

    [Fact]
    public async Task MigrateEventSignups_calls_service_and_returns_result()
    {
        var apiUniqueId = UniqueId("admin");

        var mockUsers = new Mock<IUserRepository>();
        mockUsers
            .Setup(r => r.GetByUniqueIdAsync(apiUniqueId))
            .ReturnsAsync(
                new User
                {
                    UniqueId = apiUniqueId,
                    GivenName = "Admin",
                    Surname = "User",
                    Roles = new List<User.Role> { User.Role.Administrator },
                }
            );

        var migrationResult = new EventSignupMigrationResult
        {
            EventsScanned = 5,
            SignupsConverted = 2,
            SignupsRemoved = 1,
        };

        var mockConversion = new Mock<IEventSignupConversionService>();
        mockConversion
            .Setup(s => s.MigrateAllUserSignupsAsync(It.IsAny<IUserRepository>(), It.IsAny<IHomeRepository>()))
            .ReturnsAsync(migrationResult);

        var mockAudit = new Mock<IAuditLogRepository>();
        mockAudit.Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var c = CreateController(mockUsers.Object, Mock.Of<IHomeRepository>(), mockAudit.Object, mockConversion.Object, nameId: "admin");
        var result = await c.MigrateEventSignups();

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<EventSignupMigrationResult>(ok.Value);
        Assert.Equal(2, body.SignupsConverted);
        Assert.Equal(1, body.SignupsRemoved);

        mockAudit.Verify(
            a => a.AddAsync(It.Is<NewAuditLogEntry>(e =>
                e.SubjectId == "migrate-event-signups"
                && e.Action.Contains("2 converted")
            )),
            Times.Once
        );
    }
}
