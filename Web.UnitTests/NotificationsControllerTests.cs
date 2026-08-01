using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
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
        IEnumerable<Committee>? committees = null,
        INotificationService? service = null
    )
    {
        service ??= new NotificationService(
            new MockNotificationRepository(),
            new NoOpNotificationRealtimeNotifier(),
            NullLogger<NotificationService>.Instance
        );

        var userRepo = new Mock<IUserRepository>();
        userRepo.Setup(r => r.GetByUniqueIdAsync(It.IsAny<string>())).ReturnsAsync(apiUser);

        var committeeRepo = new Mock<ICommitteeRepository>();
        committeeRepo.Setup(r => r.GetAllAsync()).ReturnsAsync((committees ?? Array.Empty<Committee>()).ToList());
        var committeeCache = new CommitteeListCache(committeeRepo.Object, new MemoryCache(new MemoryCacheOptions()));

        var controller = new NotificationsController(service, userRepo.Object, committeeCache)
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
    public async Task GetMine_ReturnsOnlyNotificationsForAudiencesCallerBelongsTo()
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
    public async Task GetMine_OrdersNotificationsNewestFirstAcrossAudiences()
    {
        // Seed via a stubbed service so the CreatedUtc of each audience's notification is deterministic.
        var older = new Notification
        {
            Id = Guid.NewGuid(),
            AudienceKey = NotificationAudience.Administrators,
            TargetId = "older",
            CreatedUtc = DateTime.UtcNow.AddMinutes(-10),
        };
        var newer = new Notification
        {
            Id = Guid.NewGuid(),
            AudienceKey = NotificationAudience.Committee("c1"),
            TargetId = "newer",
            CreatedUtc = DateTime.UtcNow,
        };
        var service = new Mock<INotificationService>();
        service.Setup(s => s.GetUnresolvedForAudienceAsync(NotificationAudience.Administrators, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Notification> { older });
        service.Setup(s => s.GetUnresolvedForAudienceAsync(NotificationAudience.Committee("c1"), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Notification> { newer });

        var committee = new Committee { Id = "c1", ManagementRole = User.Role.GardenClub };
        var (controller, _) = CreateController(Admin(), new[] { committee }, service.Object);

        var result = await controller.GetMine();

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsAssignableFrom<IEnumerable<NotificationPresentation>>(ok.Value).ToList();
        Assert.Equal(new[] { "newer", "older" }, payload.Select(p => p.TargetId).ToArray());
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
    public async Task Unacknowledge_ReopensAcknowledgedNotification()
    {
        var (controller, service) = CreateController(Admin());
        var raised = await service.RaiseAsync(NotificationType.Registration, NotificationAudience.Administrators, NotificationTargetType.User, "user-1", "New user", "Jane");
        await controller.Acknowledge(raised.Id);

        var result = await controller.Unacknowledge(raised.Id);

        Assert.IsType<OkObjectResult>(result);
        var stored = await service.GetByIdAsync(raised.Id);
        Assert.Null(stored!.ResolvedUtc);
        Assert.Null(stored.ResolvedBy);
    }

    [Fact]
    public async Task Unacknowledge_IsIdempotent_WhenNotResolved()
    {
        var (controller, service) = CreateController(Admin());
        var raised = await service.RaiseAsync(NotificationType.Registration, NotificationAudience.Administrators, NotificationTargetType.User, "user-1", "New user", "Jane");

        var result = await controller.Unacknowledge(raised.Id);

        Assert.IsType<OkObjectResult>(result);
        var stored = await service.GetByIdAsync(raised.Id);
        Assert.Null(stored!.ResolvedUtc);
    }

    [Fact]
    public async Task Unacknowledge_ReturnsBadRequest_ForTypeWithModerationAction()
    {
        var committee = new Committee { Id = "c1", ManagementRole = User.Role.GardenClub };
        var (controller, service) = CreateController(Admin(), new[] { committee });
        var raised = await service.RaiseAsync(NotificationType.HeldMessage, NotificationAudience.Committee("c1"), NotificationTargetType.HeldMessage, "held-1", "Held", "x");
        // Resolve via the moderation path, then confirm the undo endpoint refuses to re-open it.
        await service.ResolveAsync(NotificationTargetType.HeldMessage, "held-1", "moderator-1");

        var result = await controller.Unacknowledge(raised.Id);

        Assert.IsType<BadRequestObjectResult>(result);
        var stored = await service.GetByIdAsync(raised.Id);
        Assert.NotNull(stored!.ResolvedUtc); // still resolved
    }

    [Fact]
    public async Task Unacknowledge_ReturnsNotFound_WhenMissing()
    {
        var (controller, _) = CreateController(Admin());

        var result = await controller.Unacknowledge(Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Unacknowledge_ReturnsForbid_WhenCallerNotInAudience()
    {
        var admin = CreateController(Admin());
        var raised = await admin.service.RaiseAsync(NotificationType.Registration, NotificationAudience.Administrators, NotificationTargetType.User, "user-1", "New user", "Jane");
        await admin.controller.Acknowledge(raised.Id);

        var (outsider, _) = CreateController(
            new User { UniqueId = "resident-1", Roles = new List<User.Role> { User.Role.Resident } },
            service: admin.service
        );

        var result = await outsider.Unacknowledge(raised.Id);

        Assert.IsType<ForbidResult>(result);
        var stored = await admin.service.GetByIdAsync(raised.Id);
        Assert.NotNull(stored!.ResolvedUtc); // untouched
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

    /// <summary>
    /// A service whose ETag retry budget is exhausted rethrows the final 412; the endpoint must
    /// surface that as a retryable 409, not an unhandled 500.
    /// </summary>
    [Fact]
    public async Task Acknowledge_Returns409_WhenConcurrencyRetriesExhausted()
    {
        var (controller, _) = CreateController(Admin(), service: ExhaustedConcurrencyService(acknowledge: true).Object);

        var result = await controller.Acknowledge(Guid.NewGuid());

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, status.StatusCode);
    }

    [Fact]
    public async Task Unacknowledge_Returns409_WhenConcurrencyRetriesExhausted()
    {
        var (controller, _) = CreateController(Admin(), service: ExhaustedConcurrencyService(acknowledge: false).Object);

        var result = await controller.Unacknowledge(Guid.NewGuid());

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, status.StatusCode);
    }

    /// <summary>
    /// An INotificationService stub that returns an acknowledgeable Administrators-audience
    /// notification from GetByIdAsync but throws the post-retry 412 from the requested write path.
    /// </summary>
    private static Mock<INotificationService> ExhaustedConcurrencyService(bool acknowledge)
    {
        var existing = new Notification
        {
            Id = Guid.NewGuid(),
            Type = NotificationType.Registration,
            AudienceKey = NotificationAudience.Administrators,
        };
        var contention = new Microsoft.Azure.Cosmos.CosmosException(
            "ETag mismatch.", System.Net.HttpStatusCode.PreconditionFailed, 0, string.Empty, 0);

        var service = new Mock<INotificationService>();
        service.Setup(s => s.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        if (acknowledge)
            service.Setup(s => s.AcknowledgeAsync(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(contention);
        else
            service.Setup(s => s.UnacknowledgeAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(contention);
        return service;
    }
}
