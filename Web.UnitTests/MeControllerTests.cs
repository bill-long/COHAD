using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Web.Controllers;
using Web.MockData;
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
        IResidentRepository? residentRepository = null,
        INotificationService? notificationService = null,
        string nameId = "u1",
        string idp = "google.com"
    )
    {
        residentRepository ??= CreateDefaultResidentMock();
        notificationService ??= new NotificationService(
            new MockNotificationRepository(),
            new NoOpNotificationRealtimeNotifier(),
            NullLogger<NotificationService>.Instance
        );

        var controller = new MeController(
            userRepository,
            homeRepository,
            residentRepository,
            notificationService,
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
    public async Task RaiseNewUserNotification_raises_registration_for_administrators()
    {
        var notifications = new Mock<INotificationService>();
        notifications
            .Setup(s => s.RaiseAsync(
                It.IsAny<NotificationType>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Notification());

        var controller = CreateController(
            Mock.Of<IUserRepository>(),
            Mock.Of<IHomeRepository>(),
            notificationService: notifications.Object
        );

        var newUser = new User
        {
            UniqueId = "google.comu9",
            GivenName = "Jane",
            Surname = "Doe",
            StreetAddress = "123 Mock Lane",
            Emails = "jane@example.com",
        };

        await controller.RaiseNewUserNotification(newUser);

        notifications.Verify(s => s.RaiseAsync(
            NotificationType.Registration,
            NotificationAudience.Administrators,
            NotificationTargetType.User,
            "google.comu9",
            "New user registered",
            It.Is<string>(summary => summary.Contains("Jane Doe") && summary.Contains("123 Mock Lane")),
            "/manage/users",
            It.IsAny<CancellationToken>()), Times.Once);
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
}
