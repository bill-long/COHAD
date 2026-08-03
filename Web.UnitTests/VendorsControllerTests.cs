using System;
using System.Collections.Generic;
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
            It.Is<string>(deepLink => deepLink == $"/residents/vendors/{vendorId:D}"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateFlag_duplicate_pending_reraises_notification_to_heal_a_prior_failed_raise()
    {
        // A prior attempt saved the flag but failed before raising the notification. Because there is no
        // background sweep that re-raises vendor-flag notifications, the duplicate path must re-raise
        // (idempotently) so the flag still reaches administrators — otherwise it is invisible forever.
        var userRepo = new Mock<IUserRepository>(MockBehavior.Strict);
        var vendorRepo = new Mock<IVendorRepository>(MockBehavior.Strict);
        var reviewRepo = new Mock<IVendorReviewRepository>(MockBehavior.Strict);
        var auditRepo = new Mock<IAuditLogRepository>(MockBehavior.Strict);
        var flagRepo = new Mock<IVendorFlagRepository>(MockBehavior.Strict);
        var notifications = new Mock<INotificationService>();
        var vendorId = Guid.NewGuid();
        var existingFlagId = Guid.NewGuid();

        userRepo.Setup(r => r.GetByUniqueIdAsync("idpuser-1"))
            .ReturnsAsync(new User { UniqueId = "idpuser-1", GivenName = "Alex", Surname = "Resident", Roles = new List<User.Role> { User.Role.Resident } });
        vendorRepo.Setup(r => r.GetByIdAsync(vendorId)).ReturnsAsync(new Vendor { Id = vendorId, Name = "Acme" });
        flagRepo.Setup(r => r.GetPendingByAuthorAsync(vendorId, "idpuser-1"))
            .ReturnsAsync(new VendorFlag { Id = existingFlagId, VendorId = vendorId, FlagNote = "spam", Status = "Pending" });

        var controller = BuildController(userRepo.Object, vendorRepo.Object, reviewRepo.Object, auditRepo.Object, flagRepo.Object, notifications.Object);
        var result = await controller.CreateFlag(vendorId, new VendorFlagRequest { FlagNote = "spam again" });

        Assert.IsType<ConflictObjectResult>(result);
        // No new flag is written, but the notification is re-raised on the existing flag's id.
        flagRepo.Verify(r => r.UpsertAsync(It.IsAny<VendorFlag>()), Times.Never);
        notifications.Verify(s => s.RaiseAsync(
            NotificationType.VendorFlag,
            NotificationAudience.Administrators,
            NotificationTargetType.VendorFlag,
            It.Is<string>(t => t == existingFlagId.ToString("D")),
            "Vendor flagged",
            It.Is<string>(summary => summary.Contains("Acme")),
            It.Is<string>(deepLink => deepLink == $"/residents/vendors/{vendorId:D}"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateFlag_duplicate_pending_still_returns_409_when_the_heal_raise_throws()
    {
        // The duplicate-path re-raise is best-effort: a transient notification-store failure must not
        // turn the deterministic 409 into a 500 for a resident who already has a pending report.
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
        flagRepo.Setup(r => r.GetPendingByAuthorAsync(vendorId, "idpuser-1"))
            .ReturnsAsync(new VendorFlag { Id = Guid.NewGuid(), VendorId = vendorId, FlagNote = "spam", Status = "Pending" });
        notifications.Setup(s => s.RaiseAsync(
            It.IsAny<NotificationType>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("notification store unavailable"));

        var controller = BuildController(userRepo.Object, vendorRepo.Object, reviewRepo.Object, auditRepo.Object, flagRepo.Object, notifications.Object);
        var result = await controller.CreateFlag(vendorId, new VendorFlagRequest { FlagNote = "spam again" });

        Assert.IsType<ConflictObjectResult>(result);
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
        var controller = new VendorsController(
            vendorRepository,
            reviewRepository,
            flagRepository,
            new CurrentUserAccessor(userRepository),
            auditLogRepository,
            notificationService,
            NullLogger<VendorsController>.Instance
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
}
