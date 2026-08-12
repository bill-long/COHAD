using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
        string idp = "google.com",
        IResidentRepository residents = null
    )
    {
        var c = new UserController(users, new CurrentUserAccessor(users), homes, residents ?? Mock.Of<IResidentRepository>(), audit, signupConversion ?? Mock.Of<IEventSignupConversionService>(), NullLogger<UserController>.Instance)
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
    public async Task UpdateUserProperties_propagates_conflict_for_the_409_filter_without_auditing()
    {
        var apiUniqueId = UniqueId("admin");

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
            .Setup(r => r.GetByUniqueIdAsync("target-user"))
            .ReturnsAsync(new User { UniqueId = "target-user", Emails = "target@example.com" });
        mockUsers
            .Setup(r => r.UpsertAsync(It.IsAny<User>()))
            .ThrowsAsync(
                ConcurrencyConflictException.For("User", "target-user", new InvalidOperationException("ETag mismatch"))
            );

        var mockAudit = new Mock<IAuditLogRepository>();

        var c = CreateController(mockUsers.Object, Mock.Of<IHomeRepository>(), mockAudit.Object, nameId: "admin");

        // The exception must escape the action (the global ConcurrencyConflictExceptionFilter maps
        // it to the 409 refresh guidance) - and write-then-audit means no audit entry may describe
        // the change that never happened.
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            c.UpdateUserProperties(new UpdatedUser { UniqueId = "target-user", GivenName = "New", Surname = "Name" })
        );
        mockAudit.Verify(r => r.AddAsync(It.IsAny<NewAuditLogEntry>()), Times.Never);
    }

    [Fact]
    public async Task UpdateUserAssociations_propagates_conflict_for_the_409_filter_without_auditing()
    {
        var apiUniqueId = UniqueId("admin");

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
            .Setup(r => r.GetByUniqueIdAsync("target-user"))
            .ReturnsAsync(
                new User
                {
                    UniqueId = "target-user",
                    Emails = "target@example.com",
                    Roles = new List<User.Role>(),
                    OwnedHomeIds = new List<Guid>(),
                }
            );
        mockUsers
            .Setup(r => r.UpsertAsync(It.IsAny<User>()))
            .ThrowsAsync(
                ConcurrencyConflictException.For("User", "target-user", new InvalidOperationException("ETag mismatch"))
            );

        var mockHomes = new Mock<IHomeRepository>();
        mockHomes.Setup(r => r.GetByIdsAsync(It.IsAny<List<Guid>>())).ReturnsAsync(new List<Home>());

        var mockAudit = new Mock<IAuditLogRepository>();
        var mockConversion = new Mock<IEventSignupConversionService>();

        var c = CreateController(mockUsers.Object, mockHomes.Object, mockAudit.Object, mockConversion.Object, nameId: "admin");

        // The exception must escape the action (the global ConcurrencyConflictExceptionFilter maps
        // it to the 409 refresh guidance). The write was not applied: no audit entry, and no signup
        // conversion for a home assignment that never happened.
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            c.UpdateUserAssociations("target-user", new UpdatedUserAssociations { RoleNames = new List<string> { "Resident" } })
        );
        mockAudit.Verify(r => r.AddAsync(It.IsAny<NewAuditLogEntry>()), Times.Never);
        mockConversion.Verify(
            s => s.ConvertUserSignupsToHomeAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>()),
            Times.Never
        );
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
    public async Task UpdateUserAssociations_does_not_convert_signups_when_multiple_homes_assigned()
    {
        var apiUniqueId = UniqueId("admin");
        var targetUniqueId = "target-user";
        var homeId1 = Guid.NewGuid();
        var homeId2 = Guid.NewGuid();

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
            .ReturnsAsync(new List<Home>
            {
                new Home { Id = homeId1, StreetNumber = 42, StreetName = "Oak Ave", Residents = new List<Resident>() },
                new Home { Id = homeId2, StreetNumber = 99, StreetName = "Elm St", Residents = new List<Resident>() },
            });

        var mockAudit = new Mock<IAuditLogRepository>();
        mockAudit.Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var mockConversion = new Mock<IEventSignupConversionService>();

        var c = CreateController(mockUsers.Object, mockHomes.Object, mockAudit.Object, mockConversion.Object, nameId: "admin");
        var result = await c.UpdateUserAssociations(
            targetUniqueId,
            new UpdatedUserAssociations
            {
                RoleNames = new List<string> { "Resident" },
                OwnedHomeIds = new List<Guid> { homeId1, homeId2 },
            }
        );

        Assert.IsType<OkResult>(result);
        mockConversion.Verify(
            s => s.ConvertUserSignupsToHomeAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>()),
            Times.Never
        );
    }

    // ── Resident link ───────────────────────────────────────────────────

    private static (Mock<IUserRepository> users, Mock<IHomeRepository> homes, Mock<IAuditLogRepository> audit) LinkTestMocks(
        string targetUniqueId,
        Guid homeId,
        Guid? existingResidentLink = null
    )
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
        mockUsers
            .Setup(r => r.GetByUniqueIdAsync(targetUniqueId))
            .ReturnsAsync(
                new User
                {
                    UniqueId = targetUniqueId,
                    Emails = "target@example.com",
                    Roles = new List<User.Role>(),
                    OwnedHomeIds = new List<Guid>(),
                    ResidentId = existingResidentLink,
                }
            );
        mockUsers.Setup(r => r.UpsertAsync(It.IsAny<User>())).ReturnsAsync((User u) => u);

        var mockHomes = new Mock<IHomeRepository>();
        mockHomes
            .Setup(r => r.GetByIdsAsync(It.IsAny<List<Guid>>()))
            .ReturnsAsync(
                new List<Home>
                {
                    new Home { Id = homeId, StreetNumber = 1, StreetName = "Main", Residents = new List<Resident>() },
                }
            );

        var mockAudit = new Mock<IAuditLogRepository>();
        mockAudit.Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        return (mockUsers, mockHomes, mockAudit);
    }

    [Fact]
    public async Task UpdateUserAssociations_saves_resident_link_for_resident_in_owned_home()
    {
        var targetUniqueId = "target-user";
        var homeId = Guid.NewGuid();
        var residentId = Guid.NewGuid();
        var (mockUsers, mockHomes, mockAudit) = LinkTestMocks(targetUniqueId, homeId);

        User upserted = null;
        mockUsers
            .Setup(r => r.UpsertAsync(It.IsAny<User>()))
            .Callback<User>(u => upserted = u)
            .ReturnsAsync((User u) => u);

        var mockResidents = new Mock<IResidentRepository>();
        mockResidents
            .Setup(r => r.GetByIdAsync(residentId))
            .ReturnsAsync(new Resident { Id = residentId, HomeId = homeId });

        var c = CreateController(mockUsers.Object, mockHomes.Object, mockAudit.Object, nameId: "admin", residents: mockResidents.Object);
        var result = await c.UpdateUserAssociations(
            targetUniqueId,
            new UpdatedUserAssociations
            {
                RoleNames = new List<string> { "Resident" },
                OwnedHomeIds = new List<Guid> { homeId },
                ResidentId = residentId,
            }
        );

        Assert.IsType<OkResult>(result);
        Assert.Equal(residentId, upserted!.ResidentId);
    }

    [Fact]
    public async Task UpdateUserAssociations_rejects_resident_link_outside_owned_homes()
    {
        var targetUniqueId = "target-user";
        var homeId = Guid.NewGuid();
        var residentId = Guid.NewGuid();
        var (mockUsers, mockHomes, mockAudit) = LinkTestMocks(targetUniqueId, homeId);

        var mockResidents = new Mock<IResidentRepository>();
        mockResidents
            .Setup(r => r.GetByIdAsync(residentId))
            .ReturnsAsync(new Resident { Id = residentId, HomeId = Guid.NewGuid() });

        var c = CreateController(mockUsers.Object, mockHomes.Object, mockAudit.Object, nameId: "admin", residents: mockResidents.Object);
        var result = await c.UpdateUserAssociations(
            targetUniqueId,
            new UpdatedUserAssociations
            {
                RoleNames = new List<string> { "Resident" },
                OwnedHomeIds = new List<Guid> { homeId },
                ResidentId = residentId,
            }
        );

        Assert.IsType<BadRequestObjectResult>(result);
        mockUsers.Verify(r => r.UpsertAsync(It.IsAny<User>()), Times.Never);
    }

    [Fact]
    public async Task UpdateUserAssociations_rejects_link_to_nonexistent_resident()
    {
        var targetUniqueId = "target-user";
        var homeId = Guid.NewGuid();
        var (mockUsers, mockHomes, mockAudit) = LinkTestMocks(targetUniqueId, homeId);

        var mockResidents = new Mock<IResidentRepository>();
        mockResidents.Setup(r => r.GetByIdAsync(It.IsAny<Guid>())).ReturnsAsync((Resident)null);

        var c = CreateController(mockUsers.Object, mockHomes.Object, mockAudit.Object, nameId: "admin", residents: mockResidents.Object);
        var result = await c.UpdateUserAssociations(
            targetUniqueId,
            new UpdatedUserAssociations
            {
                RoleNames = new List<string> { "Resident" },
                OwnedHomeIds = new List<Guid> { homeId },
                ResidentId = Guid.NewGuid(),
            }
        );

        Assert.IsType<BadRequestObjectResult>(result);
        mockUsers.Verify(r => r.UpsertAsync(It.IsAny<User>()), Times.Never);
    }

    [Fact]
    public async Task UpdateUserAssociations_null_resident_link_leaves_existing_link_unchanged()
    {
        // Null (or an omitted property, which binds to null) must NOT clear the link - otherwise a
        // client that predates the resident link wipes it on every roles/homes save.
        var targetUniqueId = "target-user";
        var homeId = Guid.NewGuid();
        var existingLink = Guid.NewGuid();
        var (mockUsers, mockHomes, mockAudit) = LinkTestMocks(targetUniqueId, homeId, existingResidentLink: existingLink);

        User upserted = null;
        mockUsers
            .Setup(r => r.UpsertAsync(It.IsAny<User>()))
            .Callback<User>(u => upserted = u)
            .ReturnsAsync((User u) => u);

        var mockResidents = new Mock<IResidentRepository>();
        mockResidents
            .Setup(r => r.GetByIdAsync(existingLink))
            .ReturnsAsync(new Resident { Id = existingLink, HomeId = homeId });

        var c = CreateController(mockUsers.Object, mockHomes.Object, mockAudit.Object, nameId: "admin", residents: mockResidents.Object);
        var result = await c.UpdateUserAssociations(
            targetUniqueId,
            new UpdatedUserAssociations
            {
                RoleNames = new List<string> { "Resident" },
                OwnedHomeIds = new List<Guid> { homeId },
                ResidentId = null,
            }
        );

        Assert.IsType<OkResult>(result);
        Assert.Equal(existingLink, upserted!.ResidentId);
    }

    [Fact]
    public async Task UpdateUserAssociations_empty_guid_clears_existing_link()
    {
        var targetUniqueId = "target-user";
        var homeId = Guid.NewGuid();
        var (mockUsers, mockHomes, mockAudit) = LinkTestMocks(targetUniqueId, homeId, existingResidentLink: Guid.NewGuid());

        User upserted = null;
        mockUsers
            .Setup(r => r.UpsertAsync(It.IsAny<User>()))
            .Callback<User>(u => upserted = u)
            .ReturnsAsync((User u) => u);

        var c = CreateController(mockUsers.Object, mockHomes.Object, mockAudit.Object, nameId: "admin");
        var result = await c.UpdateUserAssociations(
            targetUniqueId,
            new UpdatedUserAssociations
            {
                RoleNames = new List<string> { "Resident" },
                OwnedHomeIds = new List<Guid> { homeId },
                ResidentId = Guid.Empty,
            }
        );

        Assert.IsType<OkResult>(result);
        Assert.Null(upserted!.ResidentId);
    }

    [Fact]
    public async Task UpdateUserAssociations_rejects_link_to_child_resident()
    {
        var targetUniqueId = "target-user";
        var homeId = Guid.NewGuid();
        var residentId = Guid.NewGuid();
        var (mockUsers, mockHomes, mockAudit) = LinkTestMocks(targetUniqueId, homeId);

        var mockResidents = new Mock<IResidentRepository>();
        mockResidents
            .Setup(r => r.GetByIdAsync(residentId))
            .ReturnsAsync(new Resident { Id = residentId, HomeId = homeId, ResidentType = Resident.Type.Child });

        var c = CreateController(mockUsers.Object, mockHomes.Object, mockAudit.Object, nameId: "admin", residents: mockResidents.Object);
        var result = await c.UpdateUserAssociations(
            targetUniqueId,
            new UpdatedUserAssociations
            {
                RoleNames = new List<string> { "Resident" },
                OwnedHomeIds = new List<Guid> { homeId },
                ResidentId = residentId,
            }
        );

        Assert.IsType<BadRequestObjectResult>(result);
        mockUsers.Verify(r => r.UpsertAsync(It.IsAny<User>()), Times.Never);
    }

    [Fact]
    public async Task UpdateUserAssociations_clears_retained_link_invalidated_by_home_change()
    {
        // The admin did not touch the link (null = unchanged), but the same request unassigns the
        // home the linked resident lives in - clear the link and note it in the audit entry, don't 400.
        var targetUniqueId = "target-user";
        var homeId = Guid.NewGuid();
        var existingLink = Guid.NewGuid();
        var (mockUsers, mockHomes, mockAudit) = LinkTestMocks(targetUniqueId, homeId, existingResidentLink: existingLink);

        User upserted = null;
        mockUsers
            .Setup(r => r.UpsertAsync(It.IsAny<User>()))
            .Callback<User>(u => upserted = u)
            .ReturnsAsync((User u) => u);

        var mockResidents = new Mock<IResidentRepository>();
        mockResidents
            .Setup(r => r.GetByIdAsync(existingLink))
            .ReturnsAsync(new Resident { Id = existingLink, HomeId = Guid.NewGuid() });

        var c = CreateController(mockUsers.Object, mockHomes.Object, mockAudit.Object, nameId: "admin", residents: mockResidents.Object);
        var result = await c.UpdateUserAssociations(
            targetUniqueId,
            new UpdatedUserAssociations
            {
                RoleNames = new List<string> { "Resident" },
                OwnedHomeIds = new List<Guid> { homeId },
                ResidentId = null,
            }
        );

        Assert.IsType<OkResult>(result);
        Assert.Null(upserted!.ResidentId);
        mockAudit.Verify(
            a => a.AddAsync(It.Is<NewAuditLogEntry>(e => e.Action.Contains("Cleared the resident link"))),
            Times.Once
        );
    }

    [Fact]
    public async Task UpdateUserAssociations_retained_link_survives_transient_resident_read_failure()
    {
        // The admin did not touch the link, so its validation is hygiene: a transient resident-read
        // failure must not fail a roles/homes-only save, and must not clear the link either.
        var targetUniqueId = "target-user";
        var homeId = Guid.NewGuid();
        var existingLink = Guid.NewGuid();
        var (mockUsers, mockHomes, mockAudit) = LinkTestMocks(targetUniqueId, homeId, existingResidentLink: existingLink);

        User upserted = null;
        mockUsers
            .Setup(r => r.UpsertAsync(It.IsAny<User>()))
            .Callback<User>(u => upserted = u)
            .ReturnsAsync((User u) => u);

        var mockResidents = new Mock<IResidentRepository>();
        mockResidents
            .Setup(r => r.GetByIdAsync(existingLink))
            .ThrowsAsync(new InvalidOperationException("Cosmos error"));

        var c = CreateController(mockUsers.Object, mockHomes.Object, mockAudit.Object, nameId: "admin", residents: mockResidents.Object);
        var result = await c.UpdateUserAssociations(
            targetUniqueId,
            new UpdatedUserAssociations
            {
                RoleNames = new List<string> { "Resident" },
                OwnedHomeIds = new List<Guid> { homeId },
                ResidentId = null,
            }
        );

        Assert.IsType<OkResult>(result);
        Assert.Equal(existingLink, upserted!.ResidentId);
    }

    // ── Resident link backfill ──────────────────────────────────────────

    [Fact]
    public async Task BackfillResidentLinks_links_by_email_then_name_and_skips_children_and_linked()
    {
        var apiUniqueId = UniqueId("admin");
        var homeId = Guid.NewGuid();

        var emailMatchUser = new User
        {
            UniqueId = "email-match",
            GivenName = "Robert",
            Surname = "Smith",
            Emails = "signin@x.com; bob@home.com",
            OwnedHomeIds = new List<Guid> { homeId },
        };
        var nameMatchUser = new User
        {
            UniqueId = "name-match",
            GivenName = "Karen ",
            Surname = "Osborn",
            Emails = "karen@signin.com",
            OwnedHomeIds = new List<Guid> { homeId },
        };
        var noMatchUser = new User
        {
            UniqueId = "no-match",
            GivenName = "Mike",
            Surname = "Jones",
            Emails = "mike@gmail.com",
            OwnedHomeIds = new List<Guid> { homeId },
        };
        var alreadyLinked = new User
        {
            UniqueId = "already-linked",
            ResidentId = Guid.NewGuid(),
            OwnedHomeIds = new List<Guid> { homeId },
        };
        var homeless = new User { UniqueId = "homeless", Emails = "h@x.com" };
        // Email match and name match point at DIFFERENT residents; the email match must win.
        var precedenceUser = new User
        {
            UniqueId = "precedence",
            GivenName = "Pat",
            Surname = "Doe",
            Emails = "pat@home.com",
            OwnedHomeIds = new List<Guid> { homeId },
        };

        var adminUser = new User
        {
            UniqueId = apiUniqueId,
            GivenName = "Admin",
            Surname = "User",
            Roles = new List<User.Role> { User.Role.Administrator },
        };

        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(apiUniqueId)).ReturnsAsync(adminUser);
        mockUsers
            .Setup(r => r.GetAllAsync())
            .ReturnsAsync(new List<User> { emailMatchUser, nameMatchUser, noMatchUser, alreadyLinked, homeless, precedenceUser });
        // The backfill re-reads each user before writing; hand back the same instances.
        mockUsers.Setup(r => r.GetByUniqueIdAsync("email-match")).ReturnsAsync(emailMatchUser);
        mockUsers.Setup(r => r.GetByUniqueIdAsync("name-match")).ReturnsAsync(nameMatchUser);
        mockUsers.Setup(r => r.GetByUniqueIdAsync("precedence")).ReturnsAsync(precedenceUser);
        var upserted = new List<User>();
        mockUsers
            .Setup(r => r.UpsertAsync(It.IsAny<User>()))
            .Callback<User>(u => upserted.Add(u))
            .ReturnsAsync((User u) => u);

        var bobResident = new Resident
        {
            Id = Guid.NewGuid(),
            HomeId = homeId,
            GivenName = "Different",
            Surname = "Name",
            EmailAddresses = new List<EmailAddress> { new EmailAddress { Address = " bob@home.com " } },
        };
        // A child with Karen's exact name that must NOT be linked; the adult Karen must be.
        var karenChild = new Resident
        {
            Id = Guid.NewGuid(),
            HomeId = homeId,
            GivenName = "Karen",
            Surname = "Osborn",
            ResidentType = Resident.Type.Child,
        };
        var karenAdult = new Resident
        {
            Id = Guid.NewGuid(),
            HomeId = homeId,
            GivenName = "Karen",
            Surname = "Osborn ",
            EmailAddresses = new List<EmailAddress> { new EmailAddress { Address = "karen@home.com" } },
        };

        // For the precedence user: name matches patNameResident, but email matches patEmailResident.
        var patNameResident = new Resident
        {
            Id = Guid.NewGuid(),
            HomeId = homeId,
            GivenName = "Pat",
            Surname = "Doe",
            EmailAddresses = new List<EmailAddress> { new EmailAddress { Address = "other@home.com" } },
        };
        var patEmailResident = new Resident
        {
            Id = Guid.NewGuid(),
            HomeId = homeId,
            GivenName = "Completely",
            Surname = "Unrelated",
            EmailAddresses = new List<EmailAddress> { new EmailAddress { Address = "pat@home.com" } },
        };

        var mockResidents = new Mock<IResidentRepository>();
        mockResidents
            .Setup(r => r.GetByHomeIdsAsync(It.IsAny<IEnumerable<Guid>>()))
            .ReturnsAsync(new List<Resident> { karenChild, bobResident, karenAdult, patNameResident, patEmailResident });

        var mockAudit = new Mock<IAuditLogRepository>();
        mockAudit.Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var c = CreateController(mockUsers.Object, Mock.Of<IHomeRepository>(), mockAudit.Object, nameId: "admin", residents: mockResidents.Object);
        var result = await c.BackfillResidentLinks();

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<ResidentLinkBackfillResult>(ok.Value);
        // Candidates are the unlinked home-owning users: email-match, name-match, no-match, precedence.
        Assert.Equal(4, body.UsersConsidered);
        Assert.Equal(3, body.Linked);
        Assert.Equal(1, body.SkippedNoMatch);

        Assert.Equal(bobResident.Id, emailMatchUser.ResidentId);
        Assert.Equal(karenAdult.Id, nameMatchUser.ResidentId);
        Assert.Equal(patEmailResident.Id, precedenceUser.ResidentId);
        Assert.Null(noMatchUser.ResidentId);
        Assert.Equal(3, upserted.Count);
        Assert.DoesNotContain(upserted, u => u.UniqueId == "already-linked");
        mockAudit.Verify(a => a.AddAsync(It.Is<NewAuditLogEntry>(e => e.SubjectId == "backfill-resident-links")), Times.Once);
    }

    [Fact]
    public async Task BackfillResidentLinks_counts_upsert_conflict_as_skipped_and_continues()
    {
        var apiUniqueId = UniqueId("admin");
        var homeId = Guid.NewGuid();

        // Two candidates with email matches; the first loses the write race, the second succeeds.
        var conflicted = new User
        {
            UniqueId = "conflicted",
            Emails = "bob@home.com",
            OwnedHomeIds = new List<Guid> { homeId },
        };
        var linked = new User
        {
            UniqueId = "linked",
            Emails = "karen@home.com",
            OwnedHomeIds = new List<Guid> { homeId },
        };

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
        mockUsers.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<User> { conflicted, linked });
        mockUsers.Setup(r => r.GetByUniqueIdAsync("conflicted")).ReturnsAsync(conflicted);
        mockUsers.Setup(r => r.GetByUniqueIdAsync("linked")).ReturnsAsync(linked);
        mockUsers
            .Setup(r => r.UpsertAsync(conflicted))
            .ThrowsAsync(
                ConcurrencyConflictException.For("User", "conflicted", new InvalidOperationException("ETag mismatch"))
            );
        mockUsers.Setup(r => r.UpsertAsync(linked)).ReturnsAsync((User u) => u);

        var bobResident = new Resident
        {
            Id = Guid.NewGuid(),
            HomeId = homeId,
            EmailAddresses = new List<EmailAddress> { new EmailAddress { Address = "bob@home.com" } },
        };
        var karenResident = new Resident
        {
            Id = Guid.NewGuid(),
            HomeId = homeId,
            EmailAddresses = new List<EmailAddress> { new EmailAddress { Address = "karen@home.com" } },
        };
        var mockResidents = new Mock<IResidentRepository>();
        mockResidents
            .Setup(r => r.GetByHomeIdsAsync(It.IsAny<IEnumerable<Guid>>()))
            .ReturnsAsync(new List<Resident> { bobResident, karenResident });

        var mockAudit = new Mock<IAuditLogRepository>();
        mockAudit.Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var c = CreateController(mockUsers.Object, Mock.Of<IHomeRepository>(), mockAudit.Object, nameId: "admin", residents: mockResidents.Object);
        var result = await c.BackfillResidentLinks();

        // Losing the race between the fresh read and the write is the same outcome as the snapshot
        // check catching a change: skipped, not failed - a rerun picks the user up.
        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<ResidentLinkBackfillResult>(ok.Value);
        Assert.Equal(2, body.UsersConsidered);
        Assert.Equal(1, body.Linked);
        Assert.Equal(1, body.SkippedChangedDuringRun);
        Assert.Equal(karenResident.Id, linked.ResidentId);
        mockUsers.Verify(r => r.UpsertAsync(linked), Times.Once);
    }

    [Fact]
    public async Task BackfillResidentLinks_never_links_blank_names_or_zero_id_residents()
    {
        var apiUniqueId = UniqueId("admin");
        var homeId = Guid.NewGuid();

        // Account with no given name: string.Equals(null, null) is true, so without the blank-name
        // guard this would link to the half-entered resident record below.
        var namelessUser = new User
        {
            UniqueId = "nameless",
            Surname = "Smith",
            Emails = "nameless@x.com",
            OwnedHomeIds = new List<Guid> { homeId },
        };
        // Account whose email matches only a legacy zero-id record - the empty GUID is the clear
        // sentinel and must never be stored as a link.
        var zeroMatchUser = new User
        {
            UniqueId = "zero-match",
            GivenName = "Zed",
            Surname = "Zero",
            Emails = "zed@home.com",
            OwnedHomeIds = new List<Guid> { homeId },
        };

        var mockUsers = new Mock<IUserRepository>();
        mockUsers
            .Setup(r => r.GetByUniqueIdAsync(apiUniqueId))
            .ReturnsAsync(new User
            {
                UniqueId = apiUniqueId,
                GivenName = "Admin",
                Surname = "User",
                Roles = new List<User.Role> { User.Role.Administrator },
            });
        mockUsers.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<User> { namelessUser, zeroMatchUser });
        mockUsers.Setup(r => r.GetByUniqueIdAsync("nameless")).ReturnsAsync(namelessUser);
        mockUsers.Setup(r => r.GetByUniqueIdAsync("zero-match")).ReturnsAsync(zeroMatchUser);

        var mockResidents = new Mock<IResidentRepository>();
        mockResidents
            .Setup(r => r.GetByHomeIdsAsync(It.IsAny<IEnumerable<Guid>>()))
            .ReturnsAsync(new List<Resident>
            {
                new Resident { Id = Guid.NewGuid(), HomeId = homeId, GivenName = null, Surname = "Smith" },
                new Resident
                {
                    Id = Guid.Empty,
                    HomeId = homeId,
                    GivenName = "Zed",
                    Surname = "Zero",
                    EmailAddresses = new List<EmailAddress> { new EmailAddress { Address = "zed@home.com" } },
                },
            });

        var mockAudit = new Mock<IAuditLogRepository>();
        mockAudit.Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var c = CreateController(mockUsers.Object, Mock.Of<IHomeRepository>(), mockAudit.Object, nameId: "admin", residents: mockResidents.Object);
        var result = await c.BackfillResidentLinks();

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<ResidentLinkBackfillResult>(ok.Value);
        Assert.Equal(0, body.Linked);
        Assert.Equal(2, body.SkippedNoMatch);
        Assert.Null(namelessUser.ResidentId);
        Assert.Null(zeroMatchUser.ResidentId);
        mockUsers.Verify(r => r.UpsertAsync(It.IsAny<User>()), Times.Never);
    }

    [Fact]
    public async Task BackfillResidentLinks_audit_failure_still_returns_result()
    {
        var apiUniqueId = UniqueId("admin");

        var mockUsers = new Mock<IUserRepository>();
        mockUsers
            .Setup(r => r.GetByUniqueIdAsync(apiUniqueId))
            .ReturnsAsync(new User
            {
                UniqueId = apiUniqueId,
                GivenName = "Admin",
                Surname = "User",
                Roles = new List<User.Role> { User.Role.Administrator },
            });
        mockUsers.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<User>());

        var mockAudit = new Mock<IAuditLogRepository>();
        mockAudit
            .Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>()))
            .ThrowsAsync(new InvalidOperationException("audit store down"));

        var c = CreateController(mockUsers.Object, Mock.Of<IHomeRepository>(), mockAudit.Object, nameId: "admin");
        var result = await c.BackfillResidentLinks();

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.IsType<ResidentLinkBackfillResult>(ok.Value);
    }

    [Fact]
    public async Task BackfillResidentLinks_skips_user_whose_document_changed_since_snapshot()
    {
        var apiUniqueId = UniqueId("admin");
        var homeId = Guid.NewGuid();

        var snapshot = new User
        {
            UniqueId = "changed",
            GivenName = "Ann",
            Surname = "Lee",
            Emails = "ann@home.com",
            OwnedHomeIds = new List<Guid> { homeId },
        };
        // By re-read time another admin has already linked this user.
        var fresh = new User
        {
            UniqueId = "changed",
            ResidentId = Guid.NewGuid(),
            OwnedHomeIds = new List<Guid> { homeId },
        };

        var mockUsers = new Mock<IUserRepository>();
        mockUsers
            .Setup(r => r.GetByUniqueIdAsync(apiUniqueId))
            .ReturnsAsync(new User
            {
                UniqueId = apiUniqueId,
                GivenName = "Admin",
                Surname = "User",
                Roles = new List<User.Role> { User.Role.Administrator },
            });
        mockUsers.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<User> { snapshot });
        mockUsers.Setup(r => r.GetByUniqueIdAsync("changed")).ReturnsAsync(fresh);

        var mockResidents = new Mock<IResidentRepository>();
        mockResidents
            .Setup(r => r.GetByHomeIdsAsync(It.IsAny<IEnumerable<Guid>>()))
            .ReturnsAsync(new List<Resident>
            {
                new Resident
                {
                    Id = Guid.NewGuid(),
                    HomeId = homeId,
                    EmailAddresses = new List<EmailAddress> { new EmailAddress { Address = "ann@home.com" } },
                },
            });

        var mockAudit = new Mock<IAuditLogRepository>();
        mockAudit.Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var c = CreateController(mockUsers.Object, Mock.Of<IHomeRepository>(), mockAudit.Object, nameId: "admin", residents: mockResidents.Object);
        var result = await c.BackfillResidentLinks();

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<ResidentLinkBackfillResult>(ok.Value);
        Assert.Equal(0, body.Linked);
        Assert.Equal(1, body.SkippedChangedDuringRun);
        mockUsers.Verify(r => r.UpsertAsync(It.IsAny<User>()), Times.Never);
    }

    [Fact]
    public async Task UpdateUserAssociations_audit_failure_after_upsert_still_returns_Ok_and_converts_signups()
    {
        // The change is already applied when the audit write runs; failing the request then would
        // misreport a success and skip the signup conversion.
        var targetUniqueId = "target-user";
        var homeId = Guid.NewGuid();
        var (mockUsers, mockHomes, mockAudit) = LinkTestMocks(targetUniqueId, homeId);
        mockAudit
            .Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>()))
            .ThrowsAsync(new InvalidOperationException("audit store down"));

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
        mockUsers.Verify(r => r.UpsertAsync(It.IsAny<User>()), Times.Once);
        mockConversion.Verify(s => s.ConvertUserSignupsToHomeAsync(targetUniqueId, homeId, It.IsAny<string>()), Times.Once);
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
