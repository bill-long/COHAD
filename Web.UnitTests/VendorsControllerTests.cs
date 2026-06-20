using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Web.Controllers;
using Web.Hubs;
using Web.MockData;
using Web.Models;
using Web.Services;
using Web.Services.Repositories;
using Web.UpdateModels;

namespace Web.UnitTests;

public sealed class VendorsControllerTests
{
    [Fact]
    public async Task Create_returns_bad_request_when_name_missing()
    {
        var userRepo = new Mock<IUserRepository>(MockBehavior.Strict);
        var vendorRepo = new Mock<IVendorRepository>(MockBehavior.Strict);
        var reviewRepo = new Mock<IVendorReviewRepository>(MockBehavior.Strict);
        var auditRepo = new Mock<IAuditLogRepository>(MockBehavior.Strict);
        userRepo
            .Setup(r => r.GetByUniqueIdAsync("idpuser-1"))
            .ReturnsAsync(
                new User
                {
                    UniqueId = "idpuser-1",
                    Roles = new List<User.Role> { User.Role.Resident },
                }
            );

        var controller = BuildController(userRepo.Object, vendorRepo.Object, reviewRepo.Object, auditRepo.Object);
        var result = await controller.Create(new VendorUpsertRequest { Name = " " });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Create_persists_vendor_and_returns_ok()
    {
        var userRepo = new Mock<IUserRepository>(MockBehavior.Strict);
        var vendorRepo = new Mock<IVendorRepository>(MockBehavior.Strict);
        var reviewRepo = new Mock<IVendorReviewRepository>(MockBehavior.Strict);
        var auditRepo = new Mock<IAuditLogRepository>(MockBehavior.Strict);

        userRepo
            .Setup(r => r.GetByUniqueIdAsync("idpuser-1"))
            .ReturnsAsync(
                new User
                {
                    UniqueId = "idpuser-1",
                    GivenName = "Alex",
                    Surname = "Resident",
                    Roles = new List<User.Role> { User.Role.Resident },
                }
            );
        vendorRepo.Setup(r => r.UpsertAsync(It.IsAny<Vendor>())).ReturnsAsync((Vendor v) => v);
        reviewRepo.Setup(r => r.UpsertAsync(It.IsAny<VendorReview>())).ReturnsAsync((VendorReview r) => r);
        auditRepo.Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var controller = BuildController(userRepo.Object, vendorRepo.Object, reviewRepo.Object, auditRepo.Object);
        var result = await controller.Create(
            new VendorUpsertRequest
            {
                Name = "Best Plumber",
                Categories = new List<string> { "Plumbing" },
                Email = "test@example.com",
                InitialReviewText = "Solid work, fair pricing.",
            }
        );

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
        vendorRepo.Verify(r => r.UpsertAsync(It.Is<Vendor>(v => v.Name == "Best Plumber")), Times.Once);
        reviewRepo.Verify(
            r =>
                r.UpsertAsync(
                    It.Is<VendorReview>(rev =>
                        rev.ReviewText == "Solid work, fair pricing." && rev.AuthorUniqueId == "idpuser-1"
                    )
                ),
            Times.Once
        );
        auditRepo.Verify(r => r.AddAsync(It.IsAny<NewAuditLogEntry>()), Times.Once);
    }

    [Fact]
    public async Task Delete_forbids_non_creator_non_admin()
    {
        var userRepo = new Mock<IUserRepository>(MockBehavior.Strict);
        var vendorRepo = new Mock<IVendorRepository>(MockBehavior.Strict);
        var reviewRepo = new Mock<IVendorReviewRepository>(MockBehavior.Strict);
        var auditRepo = new Mock<IAuditLogRepository>(MockBehavior.Strict);
        userRepo
            .Setup(r => r.GetByUniqueIdAsync("idpuser-1"))
            .ReturnsAsync(
                new User
                {
                    UniqueId = "idpuser-1",
                    Roles = new List<User.Role> { User.Role.Resident },
                }
            );
        vendorRepo
            .Setup(r => r.GetByIdAsync(It.IsAny<Guid>()))
            .ReturnsAsync(
                new Vendor
                {
                    Id = Guid.NewGuid(),
                    Name = "Vendor A",
                    CreatedByUniqueId = "someone-else",
                }
            );

        var controller = BuildController(userRepo.Object, vendorRepo.Object, reviewRepo.Object, auditRepo.Object);
        var result = await controller.Delete(Guid.NewGuid());

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task Create_rolls_back_vendor_when_initial_review_save_fails()
    {
        var userRepo = new Mock<IUserRepository>(MockBehavior.Strict);
        var vendorRepo = new Mock<IVendorRepository>(MockBehavior.Strict);
        var reviewRepo = new Mock<IVendorReviewRepository>(MockBehavior.Strict);
        var auditRepo = new Mock<IAuditLogRepository>(MockBehavior.Strict);

        userRepo
            .Setup(r => r.GetByUniqueIdAsync("idpuser-1"))
            .ReturnsAsync(
                new User
                {
                    UniqueId = "idpuser-1",
                    GivenName = "Alex",
                    Surname = "Resident",
                    Roles = new List<User.Role> { User.Role.Resident },
                }
            );
        vendorRepo.Setup(r => r.UpsertAsync(It.IsAny<Vendor>())).ReturnsAsync((Vendor v) => v);
        reviewRepo
            .Setup(r => r.UpsertAsync(It.IsAny<VendorReview>()))
            .ThrowsAsync(new InvalidOperationException("review write failed"));
        vendorRepo.Setup(r => r.DeleteAsync(It.IsAny<Guid>())).Returns(Task.CompletedTask);

        var controller = BuildController(userRepo.Object, vendorRepo.Object, reviewRepo.Object, auditRepo.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.Create(new VendorUpsertRequest { Name = "Best Plumber", InitialReviewText = "Solid work." })
        );
        vendorRepo.Verify(r => r.DeleteAsync(It.IsAny<Guid>()), Times.Once);
    }

    [Fact]
    public async Task CreateReview_returns_bad_request_when_text_missing()
    {
        var userRepo = new Mock<IUserRepository>(MockBehavior.Strict);
        var vendorRepo = new Mock<IVendorRepository>(MockBehavior.Strict);
        var reviewRepo = new Mock<IVendorReviewRepository>(MockBehavior.Strict);
        var auditRepo = new Mock<IAuditLogRepository>(MockBehavior.Strict);
        var vendorId = Guid.NewGuid();

        userRepo
            .Setup(r => r.GetByUniqueIdAsync("idpuser-1"))
            .ReturnsAsync(
                new User
                {
                    UniqueId = "idpuser-1",
                    Roles = new List<User.Role> { User.Role.Resident },
                }
            );
        vendorRepo.Setup(r => r.GetByIdAsync(vendorId)).ReturnsAsync(new Vendor { Id = vendorId, Name = "Vendor A" });

        var controller = BuildController(userRepo.Object, vendorRepo.Object, reviewRepo.Object, auditRepo.Object);
        var result = await controller.CreateReview(vendorId, new VendorReviewUpsertRequest { ReviewText = " " });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task UpdateReview_forbids_non_author_non_admin()
    {
        var userRepo = new Mock<IUserRepository>(MockBehavior.Strict);
        var vendorRepo = new Mock<IVendorRepository>(MockBehavior.Strict);
        var reviewRepo = new Mock<IVendorReviewRepository>(MockBehavior.Strict);
        var auditRepo = new Mock<IAuditLogRepository>(MockBehavior.Strict);
        var vendorId = Guid.NewGuid();
        var reviewId = Guid.NewGuid();

        userRepo
            .Setup(r => r.GetByUniqueIdAsync("idpuser-1"))
            .ReturnsAsync(
                new User
                {
                    UniqueId = "idpuser-1",
                    Roles = new List<User.Role> { User.Role.Resident },
                }
            );
        reviewRepo
            .Setup(r => r.GetByIdAsync(vendorId, reviewId))
            .ReturnsAsync(
                new VendorReview
                {
                    Id = reviewId,
                    VendorId = vendorId,
                    AuthorUniqueId = "someone-else",
                    ReviewText = "Original",
                }
            );

        var controller = BuildController(userRepo.Object, vendorRepo.Object, reviewRepo.Object, auditRepo.Object);
        var result = await controller.UpdateReview(
            vendorId,
            reviewId,
            new VendorReviewUpsertRequest { ReviewText = "Edited" }
        );

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task UpdateReview_allows_admin()
    {
        var userRepo = new Mock<IUserRepository>(MockBehavior.Strict);
        var vendorRepo = new Mock<IVendorRepository>(MockBehavior.Strict);
        var reviewRepo = new Mock<IVendorReviewRepository>(MockBehavior.Strict);
        var auditRepo = new Mock<IAuditLogRepository>(MockBehavior.Strict);
        var vendorId = Guid.NewGuid();
        var reviewId = Guid.NewGuid();

        userRepo
            .Setup(r => r.GetByUniqueIdAsync("idpuser-1"))
            .ReturnsAsync(
                new User
                {
                    UniqueId = "idpuser-1",
                    Roles = new List<User.Role> { User.Role.Administrator },
                }
            );
        reviewRepo
            .Setup(r => r.GetByIdAsync(vendorId, reviewId))
            .ReturnsAsync(
                new VendorReview
                {
                    Id = reviewId,
                    VendorId = vendorId,
                    AuthorUniqueId = "someone-else",
                    ReviewText = "Original",
                }
            );
        reviewRepo.Setup(r => r.UpsertAsync(It.IsAny<VendorReview>())).ReturnsAsync((VendorReview v) => v);
        auditRepo.Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var controller = BuildController(userRepo.Object, vendorRepo.Object, reviewRepo.Object, auditRepo.Object);
        var result = await controller.UpdateReview(
            vendorId,
            reviewId,
            new VendorReviewUpsertRequest { ReviewText = "Edited by admin" }
        );

        Assert.IsType<OkObjectResult>(result);
        reviewRepo.Verify(r => r.UpsertAsync(It.Is<VendorReview>(v => v.ReviewText == "Edited by admin")), Times.Once);
    }

    [Fact]
    public async Task DeleteReview_allows_author_and_deletes()
    {
        var userRepo = new Mock<IUserRepository>(MockBehavior.Strict);
        var vendorRepo = new Mock<IVendorRepository>(MockBehavior.Strict);
        var reviewRepo = new Mock<IVendorReviewRepository>(MockBehavior.Strict);
        var auditRepo = new Mock<IAuditLogRepository>(MockBehavior.Strict);
        var vendorId = Guid.NewGuid();
        var reviewId = Guid.NewGuid();

        userRepo
            .Setup(r => r.GetByUniqueIdAsync("idpuser-1"))
            .ReturnsAsync(
                new User
                {
                    UniqueId = "idpuser-1",
                    Roles = new List<User.Role> { User.Role.Resident },
                }
            );
        reviewRepo
            .Setup(r => r.GetByIdAsync(vendorId, reviewId))
            .ReturnsAsync(
                new VendorReview
                {
                    Id = reviewId,
                    VendorId = vendorId,
                    AuthorUniqueId = "idpuser-1",
                    ReviewText = "Original",
                }
            );
        reviewRepo.Setup(r => r.DeleteAsync(vendorId, reviewId)).Returns(Task.CompletedTask);
        auditRepo.Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var controller = BuildController(userRepo.Object, vendorRepo.Object, reviewRepo.Object, auditRepo.Object);
        var result = await controller.DeleteReview(vendorId, reviewId);

        Assert.IsType<OkResult>(result);
        reviewRepo.Verify(r => r.DeleteAsync(vendorId, reviewId), Times.Once);
        auditRepo.Verify(r => r.AddAsync(It.IsAny<NewAuditLogEntry>()), Times.Once);
    }

    [Fact]
    public async Task CreateReview_returns_conflict_when_user_already_reviewed()
    {
        var userRepo = new Mock<IUserRepository>(MockBehavior.Strict);
        var vendorRepo = new Mock<IVendorRepository>(MockBehavior.Strict);
        var reviewRepo = new Mock<IVendorReviewRepository>(MockBehavior.Strict);
        var auditRepo = new Mock<IAuditLogRepository>(MockBehavior.Strict);
        var vendorId = Guid.NewGuid();

        userRepo
            .Setup(r => r.GetByUniqueIdAsync("idpuser-1"))
            .ReturnsAsync(
                new User
                {
                    UniqueId = "idpuser-1",
                    GivenName = "Test",
                    Surname = "User",
                    Roles = new List<User.Role> { User.Role.Resident },
                }
            );
        vendorRepo.Setup(r => r.GetByIdAsync(vendorId)).ReturnsAsync(new Vendor { Id = vendorId, Name = "Vendor A" });
        reviewRepo
            .Setup(r => r.GetByVendorAndAuthorAsync(vendorId, "idpuser-1"))
            .ReturnsAsync(
                new VendorReview
                {
                    Id = Guid.NewGuid(),
                    VendorId = vendorId,
                    AuthorUniqueId = "idpuser-1",
                    ReviewText = "Existing review",
                }
            );

        var controller = BuildController(userRepo.Object, vendorRepo.Object, reviewRepo.Object, auditRepo.Object);
        var result = await controller.CreateReview(
            vendorId,
            new VendorReviewUpsertRequest { ReviewText = "Second review attempt" }
        );

        Assert.IsType<ConflictObjectResult>(result);
    }

    [Fact]
    public async Task CreateReview_succeeds_for_new_reviewer()
    {
        var userRepo = new Mock<IUserRepository>(MockBehavior.Strict);
        var vendorRepo = new Mock<IVendorRepository>(MockBehavior.Strict);
        var reviewRepo = new Mock<IVendorReviewRepository>(MockBehavior.Strict);
        var auditRepo = new Mock<IAuditLogRepository>(MockBehavior.Strict);
        var vendorId = Guid.NewGuid();

        userRepo
            .Setup(r => r.GetByUniqueIdAsync("idpuser-1"))
            .ReturnsAsync(
                new User
                {
                    UniqueId = "idpuser-1",
                    GivenName = "Test",
                    Surname = "User",
                    Roles = new List<User.Role> { User.Role.Resident },
                }
            );
        vendorRepo.Setup(r => r.GetByIdAsync(vendorId)).ReturnsAsync(new Vendor { Id = vendorId, Name = "Vendor A" });
        reviewRepo.Setup(r => r.GetByVendorAndAuthorAsync(vendorId, "idpuser-1")).ReturnsAsync((VendorReview)null);
        reviewRepo.Setup(r => r.UpsertAsync(It.IsAny<VendorReview>())).ReturnsAsync((VendorReview v) => v);
        auditRepo.Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var controller = BuildController(userRepo.Object, vendorRepo.Object, reviewRepo.Object, auditRepo.Object);
        var result = await controller.CreateReview(
            vendorId,
            new VendorReviewUpsertRequest { ReviewText = "Great vendor!" }
        );

        Assert.IsType<OkObjectResult>(result);
        reviewRepo.Verify(r => r.UpsertAsync(It.Is<VendorReview>(v => v.ReviewText == "Great vendor!")), Times.Once);
    }

    [Fact]
    public async Task CreateFlag_raises_vendor_flag_notification_for_administrators()
    {
        var userRepo = new Mock<IUserRepository>(MockBehavior.Strict);
        var vendorRepo = new Mock<IVendorRepository>(MockBehavior.Strict);
        var reviewRepo = new Mock<IVendorReviewRepository>(MockBehavior.Strict);
        var auditRepo = new Mock<IAuditLogRepository>(MockBehavior.Strict);
        var flagRepo = new Mock<IVendorFlagRepository>(MockBehavior.Strict);
        var notifications = new Mock<INotificationService>();
        var vendorId = Guid.NewGuid();

        userRepo.Setup(r => r.GetByUniqueIdAsync("idpuser-1"))
            .ReturnsAsync(new User { UniqueId = "idpuser-1", GivenName = "Alex", Surname = "Resident", Roles = new List<User.Role> { User.Role.Resident } });
        vendorRepo.Setup(r => r.GetByIdAsync(vendorId)).ReturnsAsync(new Vendor { Id = vendorId, Name = "Acme" });
        flagRepo.Setup(r => r.GetPendingByAuthorAsync(vendorId, "idpuser-1")).ReturnsAsync((VendorFlag?)null);
        VendorFlag? savedFlag = null;
        flagRepo.Setup(r => r.UpsertAsync(It.IsAny<VendorFlag>())).ReturnsAsync((VendorFlag f) => { savedFlag = f; return f; });
        auditRepo.Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var controller = BuildController(userRepo.Object, vendorRepo.Object, reviewRepo.Object, auditRepo.Object, flagRepo.Object, notifications.Object);
        var result = await controller.CreateFlag(vendorId, new VendorFlagRequest { FlagNote = "spam" });

        Assert.IsType<OkObjectResult>(result);
        notifications.Verify(s => s.RaiseAsync(
            NotificationType.VendorFlag,
            NotificationAudience.Administrators,
            NotificationTargetType.VendorFlag,
            It.Is<string>(t => t == savedFlag!.Id.ToString("D")),
            "Vendor flagged",
            It.Is<string>(summary => summary.Contains("Acme")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DismissFlag_resolves_vendor_flag_notification()
    {
        var userRepo = new Mock<IUserRepository>(MockBehavior.Strict);
        var vendorRepo = new Mock<IVendorRepository>(MockBehavior.Strict);
        var reviewRepo = new Mock<IVendorReviewRepository>(MockBehavior.Strict);
        var auditRepo = new Mock<IAuditLogRepository>(MockBehavior.Strict);
        var flagRepo = new Mock<IVendorFlagRepository>(MockBehavior.Strict);
        var notifications = new Mock<INotificationService>();
        var vendorId = Guid.NewGuid();
        var flagId = Guid.NewGuid();

        userRepo.Setup(r => r.GetByUniqueIdAsync("idpuser-1"))
            .ReturnsAsync(new User { UniqueId = "idpuser-1", Roles = new List<User.Role> { User.Role.Administrator } });
        flagRepo.Setup(r => r.GetByIdAsync(vendorId, flagId))
            .ReturnsAsync(new VendorFlag { Id = flagId, VendorId = vendorId, Status = "Pending" });
        flagRepo.Setup(r => r.UpsertAsync(It.IsAny<VendorFlag>())).ReturnsAsync((VendorFlag f) => f);
        vendorRepo.Setup(r => r.GetByIdAsync(vendorId)).ReturnsAsync(new Vendor { Id = vendorId, Name = "Acme" });
        auditRepo.Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var controller = BuildController(userRepo.Object, vendorRepo.Object, reviewRepo.Object, auditRepo.Object, flagRepo.Object, notifications.Object);
        var result = await controller.DismissFlag(vendorId, flagId);

        Assert.IsType<OkResult>(result);
        notifications.Verify(s => s.ResolveAsync(
            NotificationTargetType.VendorFlag, flagId.ToString("D"), "idpuser-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Delete_resolves_notifications_for_cascaded_flags()
    {
        var userRepo = new Mock<IUserRepository>(MockBehavior.Strict);
        var vendorRepo = new Mock<IVendorRepository>(MockBehavior.Strict);
        var reviewRepo = new Mock<IVendorReviewRepository>(MockBehavior.Strict);
        var auditRepo = new Mock<IAuditLogRepository>(MockBehavior.Strict);
        var flagRepo = new Mock<IVendorFlagRepository>(MockBehavior.Strict);
        var notifications = new Mock<INotificationService>();
        var vendorId = Guid.NewGuid();
        var flagA = Guid.NewGuid();
        var flagB = Guid.NewGuid();

        userRepo.Setup(r => r.GetByUniqueIdAsync("idpuser-1"))
            .ReturnsAsync(new User { UniqueId = "idpuser-1", Roles = new List<User.Role> { User.Role.Administrator } });
        vendorRepo.Setup(r => r.GetByIdAsync(vendorId)).ReturnsAsync(new Vendor { Id = vendorId, Name = "Acme", CreatedByUniqueId = "someone-else" });
        reviewRepo.Setup(r => r.GetByVendorIdAsync(vendorId)).ReturnsAsync(new List<VendorReview>());
        flagRepo.Setup(r => r.GetByVendorIdAsync(vendorId))
            .ReturnsAsync(new List<VendorFlag> { new VendorFlag { Id = flagA, VendorId = vendorId }, new VendorFlag { Id = flagB, VendorId = vendorId } });
        flagRepo.Setup(r => r.DeleteByVendorCascadeAsync(vendorId, It.IsAny<Guid>())).Returns(Task.CompletedTask);
        vendorRepo.Setup(r => r.DeleteAsync(vendorId)).Returns(Task.CompletedTask);
        auditRepo.Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var controller = BuildController(userRepo.Object, vendorRepo.Object, reviewRepo.Object, auditRepo.Object, flagRepo.Object, notifications.Object);
        var result = await controller.Delete(vendorId);

        Assert.IsType<OkResult>(result);
        notifications.Verify(s => s.ResolveAsync(NotificationTargetType.VendorFlag, flagA.ToString("D"), "idpuser-1", It.IsAny<CancellationToken>()), Times.Once);
        notifications.Verify(s => s.ResolveAsync(NotificationTargetType.VendorFlag, flagB.ToString("D"), "idpuser-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    private static VendorsController BuildController(
        IUserRepository userRepository,
        IVendorRepository vendorRepository,
        IVendorReviewRepository reviewRepository,
        IAuditLogRepository auditLogRepository,
        IVendorFlagRepository? flagRepository = null,
        INotificationService? notificationService = null
    )
    {
        flagRepository ??= new Mock<IVendorFlagRepository>(MockBehavior.Loose).Object;
        notificationService ??= new NotificationService(
            new MockNotificationRepository(),
            new NoOpNotificationRealtimeNotifier(),
            NullLogger<NotificationService>.Instance
        );
        var hubContext = CreateVendorFlagHubMock();

        var controller = new VendorsController(
            vendorRepository,
            reviewRepository,
            flagRepository,
            userRepository,
            auditLogRepository,
            hubContext.Object,
            notificationService
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
                                new Claim(ClaimTypes.NameIdentifier, "user-1"),
                                new Claim("http://schemas.microsoft.com/identity/claims/identityprovider", "idp"),
                            },
                            "TestAuth"
                        )
                    ),
                },
            },
        };

        return controller;
    }

    private static Mock<IHubContext<VendorFlagNotificationsHub>> CreateVendorFlagHubMock()
    {
        var clientProxy = new Mock<IClientProxy>(MockBehavior.Loose);
        clientProxy
            .Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var clients = new Mock<IHubClients>(MockBehavior.Loose);
        clients.Setup(c => c.Group(VendorFlagNotificationsHub.AdminGroupName)).Returns(clientProxy.Object);

        var hubContext = new Mock<IHubContext<VendorFlagNotificationsHub>>(MockBehavior.Loose);
        hubContext.Setup(h => h.Clients).Returns(clients.Object);
        return hubContext;
    }
}
