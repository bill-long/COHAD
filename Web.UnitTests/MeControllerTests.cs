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

    // ── Login-time refresh (RefreshLoginSnapshotAsync) ──────────────────

    private static User LoginSnapshot() =>
        new()
        {
            UniqueId = "google.comu1",
            GivenName = "New",
            Surname = "Name",
            Emails = "new@example.com",
            LastLoggedIn = new DateTime(2026, 8, 12, 0, 0, 0, DateTimeKind.Utc),
            Roles = new List<User.Role> { User.Role.Resident },
            OwnedHomeIds = new List<Guid> { Guid.NewGuid() },
        };

    [Fact]
    public async Task RefreshLoginSnapshot_retries_once_against_the_fresh_document_on_conflict()
    {
        // Losing the race must not lose the claims sync: a changed sign-in email would otherwise
        // stay stale in the directory until the next login. The retry must re-apply the snapshot to
        // the FRESH document, so the concurrent change (here, a role edit) is not reverted.
        var snapshot = LoginSnapshot();
        var fresh = new User
        {
            UniqueId = snapshot.UniqueId,
            GivenName = "Old",
            Surname = "Name",
            Emails = "old@example.com",
            Roles = new List<User.Role> { User.Role.Resident, User.Role.Board },
            OwnedHomeIds = new List<Guid>(snapshot.OwnedHomeIds),
        };

        var users = new Mock<IUserRepository>();
        users
            .Setup(r => r.UpsertAsync(snapshot))
            .ThrowsAsync(ConcurrencyConflictException.For("User", snapshot.UniqueId, new InvalidOperationException()));
        users.Setup(r => r.GetByUniqueIdAsync(snapshot.UniqueId)).ReturnsAsync(fresh);
        users.Setup(r => r.UpsertAsync(fresh)).ReturnsAsync(fresh);

        var controller = CreateController(users.Object, Mock.Of<IHomeRepository>());
        await controller.RefreshLoginSnapshotAsync(snapshot);

        users.Verify(r => r.UpsertAsync(fresh), Times.Once);
        Assert.Equal("New", fresh.GivenName);
        Assert.Equal("new@example.com", fresh.Emails);
        Assert.Equal(snapshot.LastLoggedIn, fresh.LastLoggedIn);
        Assert.Contains(User.Role.Board, fresh.Roles);
    }

    [Fact]
    public async Task RefreshLoginSnapshot_does_not_resurrect_a_concurrently_deleted_account()
    {
        var snapshot = LoginSnapshot();

        var users = new Mock<IUserRepository>();
        users
            .Setup(r => r.UpsertAsync(It.IsAny<User>()))
            .ThrowsAsync(ConcurrencyConflictException.For("User", snapshot.UniqueId, new InvalidOperationException()));
        users.Setup(r => r.GetByUniqueIdAsync(snapshot.UniqueId)).ReturnsAsync((User?)null);

        var controller = CreateController(users.Object, Mock.Of<IHomeRepository>());
        await controller.RefreshLoginSnapshotAsync(snapshot);

        // Only the initial attempt: a deleted account must not be written back by a login stamp.
        users.Verify(r => r.UpsertAsync(It.IsAny<User>()), Times.Once);
    }

    [Fact]
    public async Task RefreshLoginSnapshot_gives_up_quietly_after_losing_twice()
    {
        // Two lost races in one refresh means heavy contention on the record; the next login
        // re-syncs, so the second conflict must not escape into FireAndForget's generic error.
        var snapshot = LoginSnapshot();

        var users = new Mock<IUserRepository>();
        users
            .Setup(r => r.UpsertAsync(It.IsAny<User>()))
            .ThrowsAsync(ConcurrencyConflictException.For("User", snapshot.UniqueId, new InvalidOperationException()));
        users.Setup(r => r.GetByUniqueIdAsync(snapshot.UniqueId)).ReturnsAsync(LoginSnapshot());

        var controller = CreateController(users.Object, Mock.Of<IHomeRepository>());
        await controller.RefreshLoginSnapshotAsync(snapshot);

        users.Verify(r => r.UpsertAsync(It.IsAny<User>()), Times.Exactly(2));
    }
}
