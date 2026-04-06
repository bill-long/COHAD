using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

public sealed class MeControllerTests
{
    private const string IdentityProviderClaim = "http://schemas.microsoft.com/identity/claims/identityprovider";

    private static IResidentRepository CreateDefaultResidentMock()
    {
        var mock = new Mock<IResidentRepository>();
        mock.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Resident>());
        mock.Setup(r => r.GetByHomeIdsAsync(It.IsAny<IEnumerable<Guid>>())).ReturnsAsync(new List<Resident>());
        return mock.Object;
    }

    private static MeController CreateController(
        IUserRepository userRepository,
        IHomeRepository homeRepository,
        IEmailJobRepository? emailJobRepository = null,
        IDocumentFileStore? fileStore = null,
        EmailJobQueue? emailJobQueue = null,
        IResidentRepository? residentRepository = null,
        string nameId = "u1",
        string idp = "google.com"
    )
    {
        residentRepository ??= CreateDefaultResidentMock();
        emailJobRepository ??= Mock.Of<IEmailJobRepository>();
        fileStore ??= Mock.Of<IDocumentFileStore>();
        emailJobQueue ??= new EmailJobQueue();
        var controller = new MeController(
            userRepository,
            homeRepository,
            residentRepository,
            emailJobRepository,
            fileStore,
            emailJobQueue,
            Mock.Of<ILogger<MeController>>()
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
                                new Claim(ClaimTypes.GivenName, "Test"),
                                new Claim(ClaimTypes.Surname, "User"),
                                new Claim(IdentityProviderClaim, idp),
                                new Claim("emails", "test@example.com"),
                            },
                            "Test"
                        )
                    ),
                },
            },
        };
        return controller;
    }

    [Fact]
    public async Task Get_existing_user_returns_without_waiting_for_last_login_upsert()
    {
        var uniqueId = "google.comu1";
        var users = new Mock<IUserRepository>();
        users
            .Setup(r => r.GetByUniqueIdAsync(uniqueId))
            .ReturnsAsync(
                new User
                {
                    UniqueId = uniqueId,
                    Roles = new List<User.Role> { User.Role.Resident },
                    OwnedHomeIds = new List<Guid>(),
                }
            );

        var neverCompletes = new TaskCompletionSource<User>();
        users.Setup(r => r.UpsertAsync(It.IsAny<User>())).Returns(neverCompletes.Task);

        var homes = new Mock<IHomeRepository>();
        homes.Setup(r => r.GetByIdsAsync(It.IsAny<List<Guid>>())).ReturnsAsync(new List<Home>());

        var controller = CreateController(users.Object, homes.Object);

        var getTask = controller.Get();
        var completed = await Task.WhenAny(getTask, Task.Delay(250));

        Assert.Same(getTask, completed);
        Assert.NotNull(await getTask);
    }

    [Fact]
    public async Task Get_user_without_roles_returns_empty_owned_homes()
    {
        var uniqueId = "google.comu1";
        var homeId = Guid.NewGuid();

        var users = new Mock<IUserRepository>();
        users
            .Setup(r => r.GetByUniqueIdAsync(uniqueId))
            .ReturnsAsync(
                new User
                {
                    UniqueId = uniqueId,
                    Roles = new List<User.Role>(),
                    OwnedHomeIds = new List<Guid> { homeId },
                }
            );
        users.Setup(r => r.UpsertAsync(It.IsAny<User>())).ReturnsAsync((User u) => u);

        var homes = new Mock<IHomeRepository>();
        var controller = CreateController(users.Object, homes.Object);

        var result = await controller.Get();

        Assert.NotNull(result);
        Assert.Empty(result.OwnedHomes);
        homes.Verify(r => r.GetByIdsAsync(It.IsAny<List<Guid>>()), Times.Never);
        users.Verify(r => r.GetAllAsync(), Times.Never);
    }

    [Fact]
    public async Task Get_user_with_null_roles_returns_empty_owned_homes()
    {
        var uniqueId = "google.comu1";
        var homeId = Guid.NewGuid();

        var users = new Mock<IUserRepository>();
        users
            .Setup(r => r.GetByUniqueIdAsync(uniqueId))
            .ReturnsAsync(
                new User
                {
                    UniqueId = uniqueId,
                    Roles = null,
                    OwnedHomeIds = new List<Guid> { homeId },
                }
            );
        users.Setup(r => r.UpsertAsync(It.IsAny<User>())).ReturnsAsync((User u) => u);

        var homes = new Mock<IHomeRepository>();
        var controller = CreateController(users.Object, homes.Object);

        var result = await controller.Get();

        Assert.NotNull(result);
        Assert.Empty(result.OwnedHomes);
        homes.Verify(r => r.GetByIdsAsync(It.IsAny<List<Guid>>()), Times.Never);
        users.Verify(r => r.GetAllAsync(), Times.Never);
    }

    [Fact]
    public async Task Get_resident_user_returns_owned_homes_with_associated_users()
    {
        var uniqueId = "google.comu1";
        var otherUniqueId = "google.comu2";
        var homeId = Guid.NewGuid();

        var users = new Mock<IUserRepository>();
        users
            .Setup(r => r.GetByUniqueIdAsync(uniqueId))
            .ReturnsAsync(
                new User
                {
                    UniqueId = uniqueId,
                    Roles = new List<User.Role> { User.Role.Resident },
                    OwnedHomeIds = new List<Guid> { homeId },
                }
            );
        users.Setup(r => r.UpsertAsync(It.IsAny<User>())).ReturnsAsync((User u) => u);
        users
            .Setup(r => r.GetAllAsync())
            .ReturnsAsync(
                new List<User>
                {
                    new User
                    {
                        UniqueId = uniqueId,
                        GivenName = "Test",
                        Surname = "User",
                        Emails = "test@example.com",
                        IdentityProvider = "google.com",
                        OwnedHomeIds = new List<Guid> { homeId },
                    },
                    new User
                    {
                        UniqueId = otherUniqueId,
                        GivenName = "Other",
                        Surname = "Owner",
                        Emails = "other@example.com",
                        IdentityProvider = "google.com",
                        OwnedHomeIds = new List<Guid> { homeId },
                    },
                }
            );

        var homes = new Mock<IHomeRepository>();
        homes
            .Setup(r => r.GetByIdsAsync(It.Is<List<Guid>>(ids => ids.Contains(homeId))))
            .ReturnsAsync(
                new List<Home>
                {
                    new Home
                    {
                        Id = homeId,
                        StreetNumber = 123,
                        StreetName = "Main St",
                    },
                }
            );

        var controller = CreateController(users.Object, homes.Object);

        var result = await controller.Get();

        Assert.NotNull(result);
        Assert.Single(result.OwnedHomes);
        Assert.Equal(2, result.OwnedHomes[0].AssociatedUsers.Count);
    }

    [Fact]
    public async Task Get_administrator_user_returns_owned_homes_with_associated_users()
    {
        var uniqueId = "google.comu1";
        var homeId = Guid.NewGuid();

        var users = new Mock<IUserRepository>();
        users
            .Setup(r => r.GetByUniqueIdAsync(uniqueId))
            .ReturnsAsync(
                new User
                {
                    UniqueId = uniqueId,
                    Roles = new List<User.Role> { User.Role.Administrator },
                    OwnedHomeIds = new List<Guid> { homeId },
                }
            );
        users.Setup(r => r.UpsertAsync(It.IsAny<User>())).ReturnsAsync((User u) => u);
        users
            .Setup(r => r.GetAllAsync())
            .ReturnsAsync(
                new List<User>
                {
                    new User
                    {
                        UniqueId = uniqueId,
                        GivenName = "Test",
                        Surname = "User",
                        Emails = "test@example.com",
                        IdentityProvider = "google.com",
                        OwnedHomeIds = new List<Guid> { homeId },
                    },
                }
            );

        var homes = new Mock<IHomeRepository>();
        homes
            .Setup(r => r.GetByIdsAsync(It.Is<List<Guid>>(ids => ids.Contains(homeId))))
            .ReturnsAsync(
                new List<Home>
                {
                    new Home
                    {
                        Id = homeId,
                        StreetNumber = 456,
                        StreetName = "Oak Ave",
                    },
                }
            );

        var controller = CreateController(users.Object, homes.Object);

        var result = await controller.Get();

        Assert.NotNull(result);
        Assert.Single(result.OwnedHomes);
        homes.Verify(r => r.GetByIdsAsync(It.IsAny<List<Guid>>()), Times.Once);
        users.Verify(r => r.GetAllAsync(), Times.Once);
    }

    [Fact]
    public async Task ResolveAdminRecipients_matches_by_email()
    {
        var homeId = Guid.NewGuid();
        var users = new Mock<IUserRepository>();
        users
            .Setup(r => r.GetAllAsync())
            .ReturnsAsync(
                new List<User>
                {
                    new User
                    {
                        UniqueId = "admin1",
                        GivenName = "Admin",
                        Surname = "One",
                        Emails = "admin@example.com",
                        Roles = new List<User.Role> { User.Role.Administrator },
                        OwnedHomeIds = new List<Guid> { homeId },
                    },
                }
            );

        var homes = new Mock<IHomeRepository>();
        homes
            .Setup(r => r.GetByIdsAsync(It.IsAny<List<Guid>>()))
            .ReturnsAsync(new List<Home> { new Home { Id = homeId } });

        var residents = new Mock<IResidentRepository>();
        residents
            .Setup(r => r.GetByHomeIdsAsync(It.IsAny<IEnumerable<Guid>>()))
            .ReturnsAsync(
                new List<Resident>
                {
                    new Resident
                    {
                        Id = Guid.NewGuid(),
                        HomeId = homeId,
                        GivenName = "Different",
                        Surname = "Name",
                        EmailAddresses = new List<EmailAddress> { new EmailAddress { Address = "admin@example.com" } },
                    },
                }
            );

        var controller = CreateController(users.Object, homes.Object, residentRepository: residents.Object);

        var result = await controller.ResolveAdminRecipients();

        Assert.Single(result);
        Assert.Equal("admin@example.com", result[0].Email);
        Assert.Equal(Guid.Empty, result[0].HomeId);
    }

    [Fact]
    public async Task ResolveAdminRecipients_falls_back_to_name_match()
    {
        var homeId = Guid.NewGuid();
        var users = new Mock<IUserRepository>();
        users
            .Setup(r => r.GetAllAsync())
            .ReturnsAsync(
                new List<User>
                {
                    new User
                    {
                        UniqueId = "admin1",
                        GivenName = "Admin",
                        Surname = "One",
                        Emails = "admin@example.com",
                        Roles = new List<User.Role> { User.Role.Administrator },
                        OwnedHomeIds = new List<Guid> { homeId },
                    },
                }
            );

        var homes = new Mock<IHomeRepository>();
        homes
            .Setup(r => r.GetByIdsAsync(It.IsAny<List<Guid>>()))
            .ReturnsAsync(new List<Home> { new Home { Id = homeId } });

        var residents = new Mock<IResidentRepository>();
        residents
            .Setup(r => r.GetByHomeIdsAsync(It.IsAny<IEnumerable<Guid>>()))
            .ReturnsAsync(
                new List<Resident>
                {
                    new Resident
                    {
                        Id = Guid.NewGuid(),
                        HomeId = homeId,
                        GivenName = "Admin",
                        Surname = "One",
                        EmailAddresses = new List<EmailAddress>
                        {
                            new EmailAddress { Address = "resident@example.com" },
                        },
                    },
                }
            );

        var controller = CreateController(users.Object, homes.Object, residentRepository: residents.Object);

        var result = await controller.ResolveAdminRecipients();

        Assert.Single(result);
        Assert.Equal("resident@example.com", result[0].Email);
    }

    [Fact]
    public async Task ResolveAdminRecipients_skips_admin_with_no_matching_resident()
    {
        var homeId = Guid.NewGuid();
        var users = new Mock<IUserRepository>();
        users
            .Setup(r => r.GetAllAsync())
            .ReturnsAsync(
                new List<User>
                {
                    new User
                    {
                        UniqueId = "admin1",
                        GivenName = "Admin",
                        Surname = "One",
                        Emails = "admin@example.com",
                        Roles = new List<User.Role> { User.Role.Administrator },
                        OwnedHomeIds = new List<Guid> { homeId },
                    },
                }
            );

        var homes = new Mock<IHomeRepository>();
        homes
            .Setup(r => r.GetByIdsAsync(It.IsAny<List<Guid>>()))
            .ReturnsAsync(new List<Home> { new Home { Id = homeId } });

        var residents = new Mock<IResidentRepository>();
        residents
            .Setup(r => r.GetByHomeIdsAsync(It.IsAny<IEnumerable<Guid>>()))
            .ReturnsAsync(
                new List<Resident>
                {
                    new Resident
                    {
                        Id = Guid.NewGuid(),
                        HomeId = homeId,
                        GivenName = "Someone",
                        Surname = "Else",
                        EmailAddresses = new List<EmailAddress> { new EmailAddress { Address = "other@example.com" } },
                    },
                }
            );

        var controller = CreateController(users.Object, homes.Object, residentRepository: residents.Object);

        var result = await controller.ResolveAdminRecipients();

        Assert.Empty(result);
    }

    [Fact]
    public async Task ResolveAdminRecipients_skips_admin_with_no_homes()
    {
        var users = new Mock<IUserRepository>();
        users
            .Setup(r => r.GetAllAsync())
            .ReturnsAsync(
                new List<User>
                {
                    new User
                    {
                        UniqueId = "admin1",
                        GivenName = "Admin",
                        Surname = "One",
                        Emails = "admin@example.com",
                        Roles = new List<User.Role> { User.Role.Administrator },
                        OwnedHomeIds = new List<Guid>(),
                    },
                }
            );

        var homes = new Mock<IHomeRepository>();
        homes.Setup(r => r.GetByIdsAsync(It.IsAny<List<Guid>>())).ReturnsAsync(new List<Home>());

        var controller = CreateController(users.Object, homes.Object);

        var result = await controller.ResolveAdminRecipients();

        Assert.Empty(result);
    }

    [Fact]
    public async Task ResolveAdminRecipients_deduplicates_emails()
    {
        var homeId1 = Guid.NewGuid();
        var homeId2 = Guid.NewGuid();
        var users = new Mock<IUserRepository>();
        users
            .Setup(r => r.GetAllAsync())
            .ReturnsAsync(
                new List<User>
                {
                    new User
                    {
                        UniqueId = "admin1",
                        GivenName = "Admin",
                        Surname = "One",
                        Emails = "shared@example.com",
                        Roles = new List<User.Role> { User.Role.Administrator },
                        OwnedHomeIds = new List<Guid> { homeId1 },
                    },
                    new User
                    {
                        UniqueId = "admin2",
                        GivenName = "Admin",
                        Surname = "Two",
                        Emails = "shared@example.com",
                        Roles = new List<User.Role> { User.Role.Administrator },
                        OwnedHomeIds = new List<Guid> { homeId2 },
                    },
                }
            );

        var homes = new Mock<IHomeRepository>();
        homes
            .Setup(r => r.GetByIdsAsync(It.IsAny<List<Guid>>()))
            .ReturnsAsync(
                new List<Home>
                {
                    new Home { Id = homeId1 },
                    new Home { Id = homeId2 },
                }
            );

        var residents = new Mock<IResidentRepository>();
        residents
            .Setup(r => r.GetByHomeIdsAsync(It.IsAny<IEnumerable<Guid>>()))
            .ReturnsAsync(
                new List<Resident>
                {
                    new Resident
                    {
                        Id = Guid.NewGuid(),
                        HomeId = homeId1,
                        GivenName = "Admin",
                        Surname = "One",
                        EmailAddresses = new List<EmailAddress> { new EmailAddress { Address = "shared@example.com" } },
                    },
                    new Resident
                    {
                        Id = Guid.NewGuid(),
                        HomeId = homeId2,
                        GivenName = "Admin",
                        Surname = "Two",
                        EmailAddresses = new List<EmailAddress> { new EmailAddress { Address = "shared@example.com" } },
                    },
                }
            );

        var controller = CreateController(users.Object, homes.Object, residentRepository: residents.Object);

        var result = await controller.ResolveAdminRecipients();

        Assert.Single(result);
        Assert.Equal("shared@example.com", result[0].Email);
    }

    [Fact]
    public async Task EnqueueNewUserNotification_creates_job_with_admin_recipients()
    {
        var homeId = Guid.NewGuid();
        var users = new Mock<IUserRepository>();
        users
            .Setup(r => r.GetAllAsync())
            .ReturnsAsync(
                new List<User>
                {
                    new User
                    {
                        UniqueId = "admin1",
                        GivenName = "Admin",
                        Surname = "One",
                        Emails = "admin@example.com",
                        Roles = new List<User.Role> { User.Role.Administrator },
                        OwnedHomeIds = new List<Guid> { homeId },
                    },
                }
            );

        var homes = new Mock<IHomeRepository>();
        homes
            .Setup(r => r.GetByIdsAsync(It.IsAny<List<Guid>>()))
            .ReturnsAsync(new List<Home> { new Home { Id = homeId } });

        var residents = new Mock<IResidentRepository>();
        residents
            .Setup(r => r.GetByHomeIdsAsync(It.IsAny<IEnumerable<Guid>>()))
            .ReturnsAsync(
                new List<Resident>
                {
                    new Resident
                    {
                        Id = Guid.NewGuid(),
                        HomeId = homeId,
                        GivenName = "Admin",
                        Surname = "One",
                        EmailAddresses = new List<EmailAddress> { new EmailAddress { Address = "admin@example.com" } },
                    },
                }
            );

        var jobRepo = new Mock<IEmailJobRepository>();
        EmailJob? capturedJob = null;
        jobRepo
            .Setup(r => r.AddAsync(It.IsAny<EmailJob>()))
            .Callback<EmailJob>(j => capturedJob = j)
            .Returns(Task.CompletedTask);

        var fileStore = new Mock<IDocumentFileStore>();
        var queue = new EmailJobQueue();

        var controller = CreateController(
            users.Object,
            homes.Object,
            emailJobRepository: jobRepo.Object,
            fileStore: fileStore.Object,
            emailJobQueue: queue,
            residentRepository: residents.Object
        );

        var newUser = new User
        {
            GivenName = "New",
            Surname = "User",
            Emails = "newuser@example.com",
            StreetAddress = "789 Elm St",
        };

        await controller.EnqueueNewUserNotification(newUser);

        Assert.NotNull(capturedJob);
        Assert.Equal("New User Registered", capturedJob.Subject);
        Assert.Equal("webservice@cohad.org", capturedJob.FromEmail);
        Assert.Null(capturedJob.Category);
        Assert.Single(capturedJob.Recipients);
        Assert.Equal("admin@example.com", capturedJob.Recipients[0].Email);
        fileStore.Verify(f => f.UploadAsync(It.IsAny<string>(), It.IsAny<Stream>(), "text/html"), Times.Once);
    }

    [Fact]
    public async Task EnqueueNewUserNotification_skips_when_no_admins()
    {
        var users = new Mock<IUserRepository>();
        users.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<User>());

        var homes = new Mock<IHomeRepository>();
        var jobRepo = new Mock<IEmailJobRepository>();
        var fileStore = new Mock<IDocumentFileStore>();
        var queue = new EmailJobQueue();

        var controller = CreateController(
            users.Object,
            homes.Object,
            emailJobRepository: jobRepo.Object,
            fileStore: fileStore.Object,
            emailJobQueue: queue
        );

        var newUser = new User
        {
            GivenName = "New",
            Surname = "User",
            Emails = "newuser@example.com",
        };

        await controller.EnqueueNewUserNotification(newUser);

        jobRepo.Verify(r => r.AddAsync(It.IsAny<EmailJob>()), Times.Never);
        fileStore.Verify(f => f.UploadAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>()), Times.Never);
    }
}
