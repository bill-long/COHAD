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
        IResidentRepository? residents = null,
        string nameId = "nid-1",
        string idp = "google.com"
    )
    {
        var c = new HomeController(
            users,
            new CurrentUserAccessor(users),
            homes,
            residents ?? Mock.Of<IResidentRepository>(),
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

    private static Mock<IResidentRepository> CreateResidentMock(Guid homeId, List<Resident>? existing = null)
    {
        var mock = new Mock<IResidentRepository>();
        mock.Setup(r => r.GetByHomeIdAsync(homeId)).ReturnsAsync(existing ?? new List<Resident>());
        mock.Setup(r => r.UpsertAsync(It.IsAny<Resident>())).ReturnsAsync((Resident r) => r);
        mock.Setup(r => r.DeleteAsync(It.IsAny<Guid>())).Returns(Task.CompletedTask);
        return mock;
    }

    [Fact]
    public async Task Update_returns_Forbid_when_not_owner_and_not_administrator()
    {
        var homeId = Guid.NewGuid();
        var uniqueId = ExpectedUniqueId("u1", "google.com");
        var mockUsers = new Mock<IUserRepository>();
        mockUsers
            .Setup(r => r.GetByUniqueIdAsync(uniqueId))
            .ReturnsAsync(
                new User
                {
                    UniqueId = uniqueId,
                    OwnedHomeIds = new List<Guid>(),
                    Roles = new List<User.Role> { User.Role.Resident },
                }
            );

        var c = CreateController(
            mockUsers.Object,
            Mock.Of<IHomeRepository>(),
            Mock.Of<IAuditLogRepository>(),
            nameId: "u1",
            idp: "google.com"
        );

        var result = await c.Update(new UpdatedHome { Id = homeId, Residents = new List<Resident>() });
        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task Update_returns_Ok_when_owner_even_if_not_administrator()
    {
        var homeId = Guid.NewGuid();
        var uniqueId = ExpectedUniqueId("u1", "google.com");
        var mockUsers = new Mock<IUserRepository>();
        mockUsers
            .Setup(r => r.GetByUniqueIdAsync(uniqueId))
            .ReturnsAsync(
                new User
                {
                    GivenName = "A",
                    Surname = "B",
                    UniqueId = uniqueId,
                    OwnedHomeIds = new List<Guid> { homeId },
                    Roles = new List<User.Role> { User.Role.Resident },
                }
            );

        var stored = new Home
        {
            Id = homeId,
            StreetNumber = 1,
            StreetName = "Main",
            Residents = new List<Resident>(),
        };
        var mockHomes = new Mock<IHomeRepository>();
        mockHomes.Setup(r => r.GetByIdAsync(homeId)).ReturnsAsync(stored);
        mockHomes.Setup(r => r.UpsertAsync(It.IsAny<Home>())).ReturnsAsync((Home h) => h);

        var mockAudit = new Mock<IAuditLogRepository>();
        mockAudit.Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var c = CreateController(
            mockUsers.Object,
            mockHomes.Object,
            mockAudit.Object,
            CreateResidentMock(homeId).Object,
            nameId: "u1"
        );

        var result = await c.Update(new UpdatedHome { Id = homeId, Residents = new List<Resident>() });
        Assert.IsType<OkResult>(result);
        mockHomes.Verify(r => r.UpsertAsync(It.IsAny<Home>()), Times.Once);
        mockAudit.Verify(r => r.AddAsync(It.IsAny<NewAuditLogEntry>()), Times.Once);
    }

    [Fact]
    public async Task Update_when_a_resident_write_fails_reports_the_failure_and_audits_it_as_partial()
    {
        // The resident block used to log and fall through, so the admin saw a successful save for
        // changes that never landed - and execution continued into the audit write, recording
        // "Updated home information." for them. The entry is not simply dropped now: the home's own
        // email/phone write already committed, so writing nothing would leave a real change to a
        // published address with no record of who made it. The log says what actually happened.
        var homeId = Guid.NewGuid();
        var uniqueId = ExpectedUniqueId("admin", "google.com");
        var mockUsers = new Mock<IUserRepository>();
        mockUsers
            .Setup(r => r.GetByUniqueIdAsync(uniqueId))
            .ReturnsAsync(
                new User
                {
                    GivenName = "Admin",
                    Surname = "User",
                    UniqueId = uniqueId,
                    OwnedHomeIds = new List<Guid>(),
                    Roles = new List<User.Role> { User.Role.Administrator },
                }
            );

        var mockHomes = new Mock<IHomeRepository>();
        mockHomes
            .Setup(r => r.GetByIdAsync(homeId))
            .ReturnsAsync(
                new Home
                {
                    Id = homeId,
                    StreetNumber = 5,
                    StreetName = "Oak",
                    Residents = new List<Resident>(),
                }
            );
        mockHomes.Setup(r => r.UpsertAsync(It.IsAny<Home>())).ReturnsAsync((Home h) => h);

        var mockResidents = CreateResidentMock(homeId);
        mockResidents
            .Setup(r => r.UpsertAsync(It.IsAny<Resident>()))
            .ThrowsAsync(new InvalidOperationException("Cosmos is unavailable."));

        var mockAudit = new Mock<IAuditLogRepository>();
        mockAudit.Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var c = CreateController(
            mockUsers.Object,
            mockHomes.Object,
            mockAudit.Object,
            mockResidents.Object,
            nameId: "admin"
        );

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            c.Update(
                new UpdatedHome
                {
                    Id = homeId,
                    Residents = new List<Resident>
                    {
                        new Resident { GivenName = "New", Surname = "Resident" },
                    },
                }
            )
        );

        // Never the unqualified success entry - that is what made the audit log unfalsifiable.
        mockAudit.Verify(
            r => r.AddAsync(It.Is<NewAuditLogEntry>(e => e.Action == "Updated home information.")),
            Times.Never
        );
        mockAudit.Verify(r => r.AddAsync(It.Is<NewAuditLogEntry>(e => e.Action.Contains("failed"))), Times.Once);
    }

    [Fact]
    public async Task Update_when_a_resident_delete_fails_reports_the_failure_too()
    {
        // Deletes run after the upserts and are the half with the larger blast radius, so they get
        // their own fact: the two are separate awaits, and a fix covering only one would still pass
        // the test above.
        var homeId = Guid.NewGuid();
        var uniqueId = ExpectedUniqueId("admin", "google.com");
        var mockUsers = new Mock<IUserRepository>();
        mockUsers
            .Setup(r => r.GetByUniqueIdAsync(uniqueId))
            .ReturnsAsync(
                new User
                {
                    GivenName = "Admin",
                    Surname = "User",
                    UniqueId = uniqueId,
                    OwnedHomeIds = new List<Guid>(),
                    Roles = new List<User.Role> { User.Role.Administrator },
                }
            );

        var mockHomes = new Mock<IHomeRepository>();
        mockHomes
            .Setup(r => r.GetByIdAsync(homeId))
            .ReturnsAsync(
                new Home
                {
                    Id = homeId,
                    StreetNumber = 5,
                    StreetName = "Oak",
                    Residents = new List<Resident>(),
                }
            );
        mockHomes.Setup(r => r.UpsertAsync(It.IsAny<Home>())).ReturnsAsync((Home h) => h);

        // One existing resident and a payload that omits them, so the diff is a delete.
        var existing = new Resident
        {
            Id = Guid.NewGuid(),
            HomeId = homeId,
            GivenName = "Departing",
        };
        var mockResidents = CreateResidentMock(homeId, new List<Resident> { existing });
        mockResidents
            .Setup(r => r.DeleteAsync(existing.Id))
            .ThrowsAsync(new InvalidOperationException("Cosmos is unavailable."));

        var mockAudit = new Mock<IAuditLogRepository>();
        mockAudit.Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var c = CreateController(
            mockUsers.Object,
            mockHomes.Object,
            mockAudit.Object,
            mockResidents.Object,
            nameId: "admin"
        );

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            c.Update(new UpdatedHome { Id = homeId, Residents = new List<Resident>() })
        );

        mockAudit.Verify(
            r => r.AddAsync(It.Is<NewAuditLogEntry>(e => e.Action == "Updated home information.")),
            Times.Never
        );

        // The positive half matters as much: a narrower catch around only the upsert loop would
        // leave a failed delete with no entry at all, and the home's committed email/phone change
        // would reach production with no record of who made it.
        mockAudit.Verify(r => r.AddAsync(It.Is<NewAuditLogEntry>(e => e.Action.Contains("failed"))), Times.Once);
    }

    [Fact]
    public async Task Update_when_the_audit_write_also_fails_still_surfaces_the_original_fault()
    {
        // The inner guard exists so a fault writing the partial-update entry cannot displace the
        // exception in flight. Without it, the 500 the caller gets and the entry the global handler
        // logs both name the audit log, and the Cosmos resident fault - the thing an operator needs
        // to diagnose the partial update - is recorded nowhere.
        var homeId = Guid.NewGuid();
        var uniqueId = ExpectedUniqueId("admin", "google.com");
        var mockUsers = new Mock<IUserRepository>();
        mockUsers
            .Setup(r => r.GetByUniqueIdAsync(uniqueId))
            .ReturnsAsync(
                new User
                {
                    GivenName = "Admin",
                    Surname = "User",
                    UniqueId = uniqueId,
                    OwnedHomeIds = new List<Guid>(),
                    Roles = new List<User.Role> { User.Role.Administrator },
                }
            );

        var mockHomes = new Mock<IHomeRepository>();
        mockHomes
            .Setup(r => r.GetByIdAsync(homeId))
            .ReturnsAsync(
                new Home
                {
                    Id = homeId,
                    StreetNumber = 5,
                    StreetName = "Oak",
                    Residents = new List<Resident>(),
                }
            );
        mockHomes.Setup(r => r.UpsertAsync(It.IsAny<Home>())).ReturnsAsync((Home h) => h);

        var mockResidents = CreateResidentMock(homeId);
        mockResidents
            .Setup(r => r.UpsertAsync(It.IsAny<Resident>()))
            .ThrowsAsync(new InvalidOperationException("Cosmos residents are unavailable."));

        var mockAudit = new Mock<IAuditLogRepository>();
        mockAudit
            .Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>()))
            .ThrowsAsync(new InvalidOperationException("Cosmos audit log is unavailable."));

        var c = CreateController(
            mockUsers.Object,
            mockHomes.Object,
            mockAudit.Object,
            mockResidents.Object,
            nameId: "admin"
        );

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            c.Update(
                new UpdatedHome
                {
                    Id = homeId,
                    Residents = new List<Resident>
                    {
                        new Resident { GivenName = "New", Surname = "Resident" },
                    },
                }
            )
        );

        // The resident fault, not the audit fault.
        Assert.Equal("Cosmos residents are unavailable.", thrown.Message);
    }

    [Fact]
    public async Task Update_returns_Ok_when_administrator_without_ownership()
    {
        var homeId = Guid.NewGuid();
        var uniqueId = ExpectedUniqueId("admin", "google.com");
        var mockUsers = new Mock<IUserRepository>();
        mockUsers
            .Setup(r => r.GetByUniqueIdAsync(uniqueId))
            .ReturnsAsync(
                new User
                {
                    GivenName = "Admin",
                    Surname = "User",
                    UniqueId = uniqueId,
                    OwnedHomeIds = new List<Guid>(),
                    Roles = new List<User.Role> { User.Role.Administrator },
                }
            );

        var mockHomes = new Mock<IHomeRepository>();
        mockHomes
            .Setup(r => r.GetByIdAsync(homeId))
            .ReturnsAsync(
                new Home
                {
                    Id = homeId,
                    StreetNumber = 5,
                    StreetName = "Oak",
                    Residents = new List<Resident>(),
                }
            );
        mockHomes.Setup(r => r.UpsertAsync(It.IsAny<Home>())).ReturnsAsync((Home h) => h);

        var mockAudit = new Mock<IAuditLogRepository>();
        mockAudit.Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var c = CreateController(
            mockUsers.Object,
            mockHomes.Object,
            mockAudit.Object,
            CreateResidentMock(homeId).Object,
            nameId: "admin"
        );

        var result = await c.Update(new UpdatedHome { Id = homeId, Residents = new List<Resident>() });
        Assert.IsType<OkResult>(result);
    }

    [Fact]
    public async Task Update_returns_NotFound_when_home_missing()
    {
        var homeId = Guid.NewGuid();
        var uniqueId = ExpectedUniqueId("u1", "google.com");
        var mockUsers = new Mock<IUserRepository>();
        mockUsers
            .Setup(r => r.GetByUniqueIdAsync(uniqueId))
            .ReturnsAsync(
                new User
                {
                    UniqueId = uniqueId,
                    OwnedHomeIds = new List<Guid> { homeId },
                    Roles = new List<User.Role> { User.Role.Resident },
                }
            );

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
        mockUsers
            .Setup(r => r.GetByUniqueIdAsync(uniqueId))
            .ReturnsAsync(
                new User
                {
                    UniqueId = uniqueId,
                    OwnedHomeIds = new List<Guid> { homeId },
                    Roles = new List<User.Role> { User.Role.Resident },
                }
            );

        var mockHomes = new Mock<IHomeRepository>();
        mockHomes
            .Setup(r => r.GetByIdAsync(homeId))
            .ReturnsAsync(
                new Home
                {
                    Id = homeId,
                    StreetNumber = 1,
                    StreetName = "S",
                    Residents = new List<Resident>(),
                }
            );
        mockHomes.Setup(r => r.UpsertAsync(It.IsAny<Home>())).ReturnsAsync((Home h) => h);

        var mockAudit = new Mock<IAuditLogRepository>();
        mockAudit.Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var savedResidents = new List<Resident>();
        var mockResidents = new Mock<IResidentRepository>();
        mockResidents.Setup(r => r.GetByHomeIdAsync(homeId)).ReturnsAsync(new List<Resident>());
        mockResidents
            .Setup(r => r.UpsertAsync(It.IsAny<Resident>()))
            .Callback<Resident>(r => savedResidents.Add(r))
            .ReturnsAsync((Resident r) => r);
        mockResidents.Setup(r => r.DeleteAsync(It.IsAny<Guid>())).Returns(Task.CompletedTask);

        var c = CreateController(
            mockUsers.Object,
            mockHomes.Object,
            mockAudit.Object,
            mockResidents.Object,
            nameId: "u1"
        );

        await c.Update(
            new UpdatedHome
            {
                Id = homeId,
                Residents = new List<Resident>
                {
                    new Resident
                    {
                        GivenName = "Kid",
                        ResidentType = Resident.Type.Child,
                        EmailAddresses = new List<EmailAddress> { new EmailAddress { Address = "kid@test.com" } },
                        PhoneNumbers = new List<PhoneNumber> { new PhoneNumber() },
                    },
                },
            }
        );

        Assert.Single(savedResidents);
        Assert.Empty(savedResidents[0].EmailAddresses);
        Assert.Empty(savedResidents[0].PhoneNumbers);
    }

    [Fact]
    public async Task Update_filters_invalid_email_addresses()
    {
        var homeId = Guid.NewGuid();
        var uniqueId = ExpectedUniqueId("u1", "google.com");
        var mockUsers = new Mock<IUserRepository>();
        mockUsers
            .Setup(r => r.GetByUniqueIdAsync(uniqueId))
            .ReturnsAsync(
                new User
                {
                    UniqueId = uniqueId,
                    OwnedHomeIds = new List<Guid> { homeId },
                    Roles = new List<User.Role> { User.Role.Resident },
                }
            );

        var mockHomes = new Mock<IHomeRepository>();
        mockHomes
            .Setup(r => r.GetByIdAsync(homeId))
            .ReturnsAsync(
                new Home
                {
                    Id = homeId,
                    StreetNumber = 1,
                    StreetName = "S",
                    Residents = new List<Resident>(),
                }
            );
        mockHomes.Setup(r => r.UpsertAsync(It.IsAny<Home>())).ReturnsAsync((Home h) => h);

        var mockAudit = new Mock<IAuditLogRepository>();
        mockAudit.Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var savedResidents = new List<Resident>();
        var mockResidents = new Mock<IResidentRepository>();
        mockResidents.Setup(r => r.GetByHomeIdAsync(homeId)).ReturnsAsync(new List<Resident>());
        mockResidents
            .Setup(r => r.UpsertAsync(It.IsAny<Resident>()))
            .Callback<Resident>(r => savedResidents.Add(r))
            .ReturnsAsync((Resident r) => r);
        mockResidents.Setup(r => r.DeleteAsync(It.IsAny<Guid>())).Returns(Task.CompletedTask);

        var c = CreateController(
            mockUsers.Object,
            mockHomes.Object,
            mockAudit.Object,
            mockResidents.Object,
            nameId: "u1"
        );

        await c.Update(
            new UpdatedHome
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
                            new EmailAddress { Address = "   " },
                        },
                    },
                },
            }
        );

        Assert.Single(savedResidents);
        Assert.Single(savedResidents[0].EmailAddresses);
        Assert.Equal("good@example.com", savedResidents[0].EmailAddresses[0].Address);
    }

    [Fact]
    public async Task Update_drops_residents_with_empty_GivenName()
    {
        var homeId = Guid.NewGuid();
        var uniqueId = ExpectedUniqueId("u1", "google.com");
        var mockUsers = new Mock<IUserRepository>();
        mockUsers
            .Setup(r => r.GetByUniqueIdAsync(uniqueId))
            .ReturnsAsync(
                new User
                {
                    UniqueId = uniqueId,
                    OwnedHomeIds = new List<Guid> { homeId },
                    Roles = new List<User.Role> { User.Role.Resident },
                }
            );

        var mockHomes = new Mock<IHomeRepository>();
        mockHomes
            .Setup(r => r.GetByIdAsync(homeId))
            .ReturnsAsync(
                new Home
                {
                    Id = homeId,
                    StreetNumber = 1,
                    StreetName = "S",
                    Residents = new List<Resident>(),
                }
            );
        mockHomes.Setup(r => r.UpsertAsync(It.IsAny<Home>())).ReturnsAsync((Home h) => h);

        var mockAudit = new Mock<IAuditLogRepository>();
        mockAudit.Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var savedResidents = new List<Resident>();
        var mockResidents = new Mock<IResidentRepository>();
        mockResidents.Setup(r => r.GetByHomeIdAsync(homeId)).ReturnsAsync(new List<Resident>());
        mockResidents
            .Setup(r => r.UpsertAsync(It.IsAny<Resident>()))
            .Callback<Resident>(r => savedResidents.Add(r))
            .ReturnsAsync((Resident r) => r);
        mockResidents.Setup(r => r.DeleteAsync(It.IsAny<Guid>())).Returns(Task.CompletedTask);

        var c = CreateController(
            mockUsers.Object,
            mockHomes.Object,
            mockAudit.Object,
            mockResidents.Object,
            nameId: "u1"
        );

        await c.Update(
            new UpdatedHome
            {
                Id = homeId,
                Residents = new List<Resident>
                {
                    new Resident { GivenName = "Keep", ResidentType = Resident.Type.Homeowner },
                    new Resident { GivenName = "", ResidentType = Resident.Type.Homeowner },
                    new Resident { GivenName = null!, ResidentType = Resident.Type.Homeowner },
                },
            }
        );

        Assert.Single(savedResidents);
        Assert.Equal("Keep", savedResidents[0].GivenName);
    }
}
