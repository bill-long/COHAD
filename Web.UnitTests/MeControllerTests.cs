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

    [Fact]
    public async Task Get_persists_the_login_snapshot_even_when_the_homes_read_fails()
    {
        // Authentication succeeded, so a failed enrichment read (owned homes lookup) must not lose
        // the claims sync or the LastLoggedIn stamp - the request may 500, but the background
        // refresh still fires.
        var uniqueId = "google.comu1";
        var users = new Mock<IUserRepository>();
        users
            .Setup(r => r.GetByUniqueIdAsync(uniqueId))
            .ReturnsAsync(
                new User
                {
                    UniqueId = uniqueId,
                    Roles = new List<User.Role> { User.Role.Resident },
                    OwnedHomeIds = new List<Guid> { Guid.NewGuid() },
                }
            );

        var upserted = new TaskCompletionSource<User>(TaskCreationOptions.RunContinuationsAsynchronously);
        users
            .Setup(r => r.UpsertAsync(It.IsAny<User>()))
            .Callback<User>(u => upserted.TrySetResult(u))
            .ReturnsAsync((User u) => u);
        users.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<User>());

        var homes = new Mock<IHomeRepository>();
        homes
            .Setup(r => r.GetByIdsAsync(It.IsAny<List<Guid>>()))
            .ThrowsAsync(new InvalidOperationException("homes read failed"));

        var controller = CreateController(users.Object, homes.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.Get());

        var completed = await Task.WhenAny(upserted.Task, Task.Delay(5000));
        Assert.Same(upserted.Task, completed);
        Assert.Equal("test@example.com", (await upserted.Task).Emails);
    }

    /// <summary>
    /// A user whose stored state is already normalized (roles and a home), so nothing but the
    /// claims/stamp checks can trigger the background write.
    /// </summary>
    private static User SettledUser(string uniqueId, string emails = "test@example.com") =>
        new()
        {
            UniqueId = uniqueId,
            GivenName = "Test",
            Surname = "User",
            Emails = emails,
            LastLoggedIn = DateTime.UtcNow,
            Roles = new List<User.Role> { User.Role.Resident },
            OwnedHomeIds = new List<Guid> { Guid.NewGuid() },
        };

    private static Mock<IUserRepository> SettledUserRepo(string uniqueId, string emails = "test@example.com")
    {
        var users = new Mock<IUserRepository>();
        users.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(SettledUser(uniqueId, emails));
        users.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<User>());
        return users;
    }

    private static IHomeRepository EmptyHomes()
    {
        var homes = new Mock<IHomeRepository>();
        homes.Setup(r => r.GetByIdsAsync(It.IsAny<List<Guid>>())).ReturnsAsync(new List<Home>());
        return homes.Object;
    }

    [Fact]
    public async Task Get_does_not_write_when_the_claims_match_and_the_login_stamp_is_fresh()
    {
        // This endpoint runs on every page load. An unconditional write would put an ETag-guarded
        // write of the caller's own document behind every page view, which loses races against
        // that same user's foreground saves.
        var uniqueId = "google.comu1";
        var users = SettledUserRepo(uniqueId);

        var controller = CreateController(users.Object, EmptyHomes());
        await controller.Get();

        await Task.Delay(100);
        users.Verify(r => r.UpsertAsync(It.IsAny<User>()), Times.Never);
    }

    [Fact]
    public async Task Get_writes_when_a_claim_changed()
    {
        var uniqueId = "google.comu1";
        var users = SettledUserRepo(uniqueId, emails: "stale@example.com");

        var upserted = new TaskCompletionSource<User>(TaskCreationOptions.RunContinuationsAsynchronously);
        users
            .Setup(r => r.UpsertAsync(It.IsAny<User>()))
            .Callback<User>(u => upserted.TrySetResult(u))
            .ReturnsAsync((User u) => u);

        var controller = CreateController(users.Object, EmptyHomes());
        await controller.Get();

        var completed = await Task.WhenAny(upserted.Task, Task.Delay(5000));
        Assert.Same(upserted.Task, completed);
        Assert.Equal("test@example.com", (await upserted.Task).Emails);
    }

    [Fact]
    public async Task Get_writes_when_the_stored_document_is_not_normalized()
    {
        // Roles with no homes is a state UpsertAsync normalizes away. Reporting it without
        // converging the document would make the response depend on whether the hourly stamp
        // happened to be stale, so the normalization itself is a reason to write.
        var uniqueId = "google.comu1";
        var users = new Mock<IUserRepository>();
        users
            .Setup(r => r.GetByUniqueIdAsync(uniqueId))
            .ReturnsAsync(
                new User
                {
                    UniqueId = uniqueId,
                    GivenName = "Test",
                    Surname = "User",
                    Emails = "test@example.com",
                    LastLoggedIn = DateTime.UtcNow,
                    Roles = new List<User.Role> { User.Role.Resident },
                    OwnedHomeIds = new List<Guid>(),
                }
            );

        var upserted = new TaskCompletionSource<User>(TaskCreationOptions.RunContinuationsAsynchronously);
        users
            .Setup(r => r.UpsertAsync(It.IsAny<User>()))
            .Callback<User>(u => upserted.TrySetResult(u))
            .ReturnsAsync((User u) => u);

        var controller = CreateController(users.Object, EmptyHomes());
        await controller.Get();

        var completed = await Task.WhenAny(upserted.Task, Task.Delay(5000));
        Assert.Same(upserted.Task, completed);
    }

    [Fact]
    public async Task Get_writes_when_normalization_only_starts_a_purge_clock()
    {
        // An Administrator with no homes keeps their single role, so a count-only comparison sees
        // no change - but Apply starts UnassociatedSinceUtc. Without persisting that, the response
        // reports a purge clock that was never stored and the deletion countdown never starts.
        var uniqueId = "google.comu1";
        var users = new Mock<IUserRepository>();
        users
            .Setup(r => r.GetByUniqueIdAsync(uniqueId))
            .ReturnsAsync(
                new User
                {
                    UniqueId = uniqueId,
                    GivenName = "Test",
                    Surname = "User",
                    Emails = "test@example.com",
                    LastLoggedIn = DateTime.UtcNow,
                    Roles = new List<User.Role> { User.Role.Administrator },
                    OwnedHomeIds = new List<Guid>(),
                    UnassociatedSinceUtc = null,
                }
            );

        var upserted = new TaskCompletionSource<User>(TaskCreationOptions.RunContinuationsAsynchronously);
        users
            .Setup(r => r.UpsertAsync(It.IsAny<User>()))
            .Callback<User>(u => upserted.TrySetResult(u))
            .ReturnsAsync((User u) => u);

        var controller = CreateController(users.Object, EmptyHomes());
        await controller.Get();

        var completed = await Task.WhenAny(upserted.Task, Task.Delay(5000));
        Assert.Same(upserted.Task, completed);
        Assert.NotNull((await upserted.Task).UnassociatedSinceUtc);
    }

    [Fact]
    public async Task Get_keeps_stored_profile_values_when_a_claim_is_missing()
    {
        // A token without the emails claim means "no information", not "cleared" - persisting the
        // null would wipe the account's directory address with nothing able to recover it.
        var uniqueId = "google.comu1";
        var users = new Mock<IUserRepository>();
        users
            .Setup(r => r.GetByUniqueIdAsync(uniqueId))
            .ReturnsAsync(
                new User
                {
                    UniqueId = uniqueId,
                    GivenName = "Test",
                    Surname = "User",
                    Emails = "stored@example.com",
                    LastLoggedIn = DateTime.UtcNow,
                    Roles = new List<User.Role> { User.Role.Resident },
                    OwnedHomeIds = new List<Guid> { Guid.NewGuid() },
                }
            );
        users.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<User>());
        users.Setup(r => r.UpsertAsync(It.IsAny<User>())).ReturnsAsync((User u) => u);

        // Claims deliberately omit "emails".
        var controller = new MeController(
            users.Object,
            EmptyHomes(),
            CreateDefaultResidentMock(),
            new NotificationService(
                new MockNotificationRepository(),
                new NoOpNotificationRealtimeNotifier(),
                NullLogger<NotificationService>.Instance
            ),
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
                                new Claim(ClaimTypes.NameIdentifier, "u1"),
                                new Claim(ClaimTypes.GivenName, "Test"),
                                new Claim(ClaimTypes.Surname, "User"),
                                new Claim(IdentityProviderClaim, "google.com"),
                            },
                            "Test"
                        )
                    ),
                },
            },
        };

        var presentation = await controller.Get();

        Assert.Equal("stored@example.com", presentation.Email);
        users.Verify(r => r.UpsertAsync(It.Is<User>(u => u.Emails == null)), Times.Never);
    }

    [Fact]
    public async Task Get_reports_the_normalized_role_set_on_every_load()
    {
        // UpsertAsync drops the roles of a user with no homes. The response must agree with that
        // on every load, not only the ones that refresh - otherwise the same account's navigation
        // appears and disappears between page loads.
        var uniqueId = "google.comu1";
        var users = new Mock<IUserRepository>();
        users
            .Setup(r => r.GetByUniqueIdAsync(uniqueId))
            .ReturnsAsync(() =>
                new User
                {
                    UniqueId = uniqueId,
                    GivenName = "Test",
                    Surname = "User",
                    Emails = "test@example.com",
                    // Fresh stamp: nothing but the normalization can trigger a refresh.
                    LastLoggedIn = DateTime.UtcNow,
                    Roles = new List<User.Role> { User.Role.Resident },
                    OwnedHomeIds = new List<Guid>(),
                }
            );
        users.Setup(r => r.UpsertAsync(It.IsAny<User>())).ReturnsAsync((User u) => u);

        var controller = CreateController(users.Object, EmptyHomes());

        Assert.DoesNotContain("Resident", (await controller.Get()).Roles);
        Assert.DoesNotContain("Resident", (await controller.Get()).Roles);
    }
}
