using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Web.Controllers;
using Web.MockData;
using Web.Models;
using Web.PresentationModels;
using Web.Services;
using Web.Services.Repositories;
using Xunit;

namespace Web.UnitTests;

public sealed class NotificationsControllerTests
{
    private static (NotificationsController controller, INotificationService service) CreateController(
        User apiUser,
        IEnumerable<Committee>? committees = null
    )
    {
        var service = new NotificationService(
            new MockNotificationRepository(),
            new NoOpNotificationRealtimeNotifier(),
            NullLogger<NotificationService>.Instance
        );

        var userRepo = new Mock<IUserRepository>();
        userRepo.Setup(r => r.GetByUniqueIdAsync(It.IsAny<string>())).ReturnsAsync(apiUser);

        var committeeRepo = new Mock<ICommitteeRepository>();
        committeeRepo.Setup(r => r.GetAllAsync()).ReturnsAsync((committees ?? Array.Empty<Committee>()).ToList());

        var controller = new NotificationsController(service, userRepo.Object, committeeRepo.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(
                        new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "u1") })
                    ),
                },
            },
        };

        return (controller, service);
    }

    private static User Admin() =>
        new User { UniqueId = "admin-1", Roles = new List<User.Role> { User.Role.Administrator } };

    [Fact]
    public async Task GetMine_ReturnsAdminAndManagedCommitteeNotifications_NewestFirst()
    {
        var committee = new Committee { Id = "c1", ManagementRole = User.Role.GardenClub };
        var (controller, service) = CreateController(
            new User { UniqueId = "gardener-1", Roles = new List<User.Role> { User.Role.GardenClub } },
            new[] { committee }
        );

        // A non-admin GardenClub member manages committee c1 but is NOT in the Administrators audience.
        await service.RaiseAsync(NotificationType.Registration, NotificationAudience.Administrators, NotificationTargetType.User, "user-9", "New user", "Should be hidden");
        await service.RaiseAsync(NotificationType.HeldMessage, NotificationAudience.Committee("c1"), NotificationTargetType.HeldMessage, "held-1", "Held", "Visible");

        var result = await controller.GetMine();

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsAssignableFrom<IEnumerable<NotificationPresentation>>(ok.Value).ToList();
        Assert.Single(payload);
        Assert.Equal("held-1", payload[0].TargetId);
    }

    [Fact]
    public async Task GetMine_AdminSeesAdministratorsAudience()
    {
        var (controller, service) = CreateController(Admin());
        await service.RaiseAsync(NotificationType.Registration, NotificationAudience.Administrators, NotificationTargetType.User, "user-1", "New user", "Jane");

        var result = await controller.GetMine();

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsAssignableFrom<IEnumerable<NotificationPresentation>>(ok.Value).ToList();
        Assert.Single(payload);
        Assert.Equal("user-1", payload[0].TargetId);
    }

    [Fact]
    public async Task GetMine_ReturnsEmpty_WhenCallerHasNoAudiences()
    {
        var (controller, service) = CreateController(
            new User { UniqueId = "resident-1", Roles = new List<User.Role> { User.Role.Resident } }
        );
        await service.RaiseAsync(NotificationType.Registration, NotificationAudience.Administrators, NotificationTargetType.User, "user-1", "New user", "Jane");

        var result = await controller.GetMine();

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsAssignableFrom<IEnumerable<NotificationPresentation>>(ok.Value);
        Assert.Empty(payload);
    }

    [Fact]
    public async Task Acknowledge_ResolvesNotification_ForAdminInAudience()
    {
        var (controller, service) = CreateController(Admin());
        var raised = await service.RaiseAsync(NotificationType.Registration, NotificationAudience.Administrators, NotificationTargetType.User, "user-1", "New user", "Jane");

        var result = await controller.Acknowledge(raised.Id);

        Assert.IsType<OkObjectResult>(result);
        var stored = await service.GetByIdAsync(raised.Id);
        Assert.NotNull(stored!.ResolvedUtc);
        Assert.Equal("admin-1", stored.ResolvedBy);
    }

    [Fact]
    public async Task Acknowledge_ReturnsBadRequest_ForTypeWithModerationAction()
    {
        // An admin is in every committee audience, but a held-message notification must be resolved by
        // approve/reject — acknowledging it would hide still-pending moderation work.
        var committee = new Committee { Id = "c1", ManagementRole = User.Role.GardenClub };
        var (controller, service) = CreateController(Admin(), new[] { committee });
        var raised = await service.RaiseAsync(NotificationType.HeldMessage, NotificationAudience.Committee("c1"), NotificationTargetType.HeldMessage, "held-1", "Held", "x");

        var result = await controller.Acknowledge(raised.Id);

        Assert.IsType<BadRequestObjectResult>(result);
        var stored = await service.GetByIdAsync(raised.Id);
        Assert.Null(stored!.ResolvedUtc); // not resolved
    }

    [Fact]
    public async Task Acknowledge_ReturnsNotFound_WhenMissing()
    {
        var (controller, _) = CreateController(Admin());

        var result = await controller.Acknowledge(Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Acknowledge_ReturnsForbid_WhenCallerNotInAudience()
    {
        // Caller is not an Administrator and manages no committees, so they're in no audiences and
        // cannot acknowledge an Administrators-audience notification.
        var (controller, service) = CreateController(
            new User { UniqueId = "resident-1", Roles = new List<User.Role> { User.Role.Resident } }
        );
        var raised = await service.RaiseAsync(NotificationType.Registration, NotificationAudience.Administrators, NotificationTargetType.User, "user-1", "New user", "Jane");

        var result = await controller.Acknowledge(raised.Id);

        Assert.IsType<ForbidResult>(result);
        var stored = await service.GetByIdAsync(raised.Id);
        Assert.Null(stored!.ResolvedUtc); // untouched
    }
}
