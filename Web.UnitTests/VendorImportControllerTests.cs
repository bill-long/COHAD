using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Web.Controllers;
using Web.Models;
using Web.Services;
using Web.Services.Repositories;
using Web.UpdateModels;

namespace Web.UnitTests;

public sealed class VendorImportControllerTests
{
    [Fact]
    public async Task ImportReviews_returns_bad_request_when_empty()
    {
        var userRepo = new Mock<IUserRepository>(MockBehavior.Strict);
        var vendorRepo = new Mock<IVendorRepository>(MockBehavior.Strict);
        var reviewRepo = new Mock<IVendorReviewRepository>(MockBehavior.Strict);
        var youthRepo = new Mock<IYouthServiceListingRepository>(MockBehavior.Strict);
        var auditRepo = new Mock<IAuditLogRepository>(MockBehavior.Strict);
        userRepo
            .Setup(r => r.GetByUniqueIdAsync("idpuser-1"))
            .ReturnsAsync(
                new User
                {
                    UniqueId = "idpuser-1",
                    Roles = new List<User.Role> { User.Role.Administrator },
                }
            );

        var controller = BuildController(
            userRepo.Object,
            vendorRepo.Object,
            reviewRepo.Object,
            youthRepo.Object,
            auditRepo.Object
        );
        var result = await controller.ImportReviews(new List<VendorReviewImportRequest>());

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task ImportReviews_imports_valid_rows_and_skips_missing_vendor()
    {
        var userRepo = new Mock<IUserRepository>(MockBehavior.Strict);
        var vendorRepo = new Mock<IVendorRepository>(MockBehavior.Strict);
        var reviewRepo = new Mock<IVendorReviewRepository>(MockBehavior.Strict);
        var youthRepo = new Mock<IYouthServiceListingRepository>(MockBehavior.Strict);
        var auditRepo = new Mock<IAuditLogRepository>(MockBehavior.Strict);

        userRepo
            .Setup(r => r.GetByUniqueIdAsync("idpuser-1"))
            .ReturnsAsync(
                new User
                {
                    UniqueId = "idpuser-1",
                    GivenName = "Admin",
                    Surname = "User",
                    Roles = new List<User.Role> { User.Role.Administrator },
                }
            );

        var existingVendorId = Guid.NewGuid();
        vendorRepo
            .Setup(r => r.GetByIdAsync(existingVendorId))
            .ReturnsAsync(new Vendor { Id = existingVendorId, Name = "Vendor A" });
        vendorRepo.Setup(r => r.GetByIdAsync(It.Is<Guid>(g => g != existingVendorId))).ReturnsAsync((Vendor)null);
        reviewRepo.Setup(r => r.UpsertAsync(It.IsAny<VendorReview>())).ReturnsAsync((VendorReview r) => r);
        auditRepo.Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var controller = BuildController(
            userRepo.Object,
            vendorRepo.Object,
            reviewRepo.Object,
            youthRepo.Object,
            auditRepo.Object
        );
        var result = await controller.ImportReviews(
            new List<VendorReviewImportRequest>
            {
                new()
                {
                    VendorId = existingVendorId,
                    AuthorDisplayName = "Jane Referrer",
                    ReviewText = "Great service.",
                },
                new()
                {
                    VendorId = Guid.NewGuid(),
                    AuthorDisplayName = "Missing",
                    ReviewText = "Will skip.",
                },
            }
        );

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
        reviewRepo.Verify(
            r =>
                r.UpsertAsync(
                    It.Is<VendorReview>(v =>
                        v.AuthorDisplayName == "Jane Referrer"
                        && VendorReviewTimestamps.IsUnknown(v.CreatedUtc)
                        && VendorReviewTimestamps.IsUnknown(v.ModifiedUtc)
                    )
                ),
            Times.Once
        );
        auditRepo.Verify(r => r.AddAsync(It.IsAny<NewAuditLogEntry>()), Times.Once);
    }

    [Fact]
    public async Task ImportReviews_skips_null_rows_without_throwing()
    {
        var userRepo = new Mock<IUserRepository>(MockBehavior.Strict);
        var vendorRepo = new Mock<IVendorRepository>(MockBehavior.Strict);
        var reviewRepo = new Mock<IVendorReviewRepository>(MockBehavior.Strict);
        var youthRepo = new Mock<IYouthServiceListingRepository>(MockBehavior.Strict);
        var auditRepo = new Mock<IAuditLogRepository>(MockBehavior.Strict);

        userRepo
            .Setup(r => r.GetByUniqueIdAsync("idpuser-1"))
            .ReturnsAsync(
                new User
                {
                    UniqueId = "idpuser-1",
                    GivenName = "Admin",
                    Surname = "User",
                    Roles = new List<User.Role> { User.Role.Administrator },
                }
            );

        var existingVendorId = Guid.NewGuid();
        vendorRepo
            .Setup(r => r.GetByIdAsync(existingVendorId))
            .ReturnsAsync(new Vendor { Id = existingVendorId, Name = "Vendor A" });
        reviewRepo.Setup(r => r.UpsertAsync(It.IsAny<VendorReview>())).ReturnsAsync((VendorReview r) => r);
        auditRepo.Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var controller = BuildController(
            userRepo.Object,
            vendorRepo.Object,
            reviewRepo.Object,
            youthRepo.Object,
            auditRepo.Object
        );
        var result = await controller.ImportReviews(
            new List<VendorReviewImportRequest>
            {
                null!,
                new()
                {
                    VendorId = existingVendorId,
                    AuthorDisplayName = "R",
                    ReviewText = "Kept.",
                },
            }
        );

        Assert.IsType<OkObjectResult>(result);
        reviewRepo.Verify(r => r.UpsertAsync(It.IsAny<VendorReview>()), Times.Once);
        auditRepo.Verify(r => r.AddAsync(It.IsAny<NewAuditLogEntry>()), Times.Once);
    }

    [Fact]
    public async Task ImportVendorsAndReviews_imports_vendor_and_review_by_fingerprint()
    {
        var userRepo = new Mock<IUserRepository>(MockBehavior.Strict);
        var vendorRepo = new Mock<IVendorRepository>(MockBehavior.Strict);
        var reviewRepo = new Mock<IVendorReviewRepository>(MockBehavior.Strict);
        var youthRepo = new Mock<IYouthServiceListingRepository>(MockBehavior.Strict);
        var auditRepo = new Mock<IAuditLogRepository>(MockBehavior.Strict);

        userRepo
            .Setup(r => r.GetByUniqueIdAsync("idpuser-1"))
            .ReturnsAsync(
                new User
                {
                    UniqueId = "idpuser-1",
                    GivenName = "Admin",
                    Surname = "User",
                    Roles = new List<User.Role> { User.Role.Administrator },
                }
            );

        vendorRepo.Setup(r => r.UpsertAsync(It.IsAny<Vendor>())).ReturnsAsync((Vendor v) => v);
        reviewRepo.Setup(r => r.UpsertAsync(It.IsAny<VendorReview>())).ReturnsAsync((VendorReview r) => r);
        youthRepo.Setup(r => r.UpsertAsync(It.IsAny<YouthServiceListing>())).ReturnsAsync((YouthServiceListing y) => y);
        auditRepo.Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var controller = BuildController(
            userRepo.Object,
            vendorRepo.Object,
            reviewRepo.Object,
            youthRepo.Object,
            auditRepo.Object
        );
        var result = await controller.ImportVendorsAndReviews(
            new VendorSeedImportRequest
            {
                Vendors = new List<VendorSeedImportVendor>
                {
                    new()
                    {
                        Fingerprint = "abc123",
                        Name = "Vendor A",
                        Categories = new List<string> { "Plumbing" },
                        Notes = "Imported notes",
                    },
                },
                Reviews = new List<VendorSeedImportReview>
                {
                    new()
                    {
                        VendorFingerprint = "abc123",
                        AuthorDisplayName = "Referrer One",
                        ReviewText = "Excellent service.",
                    },
                },
                YouthServices = new List<VendorSeedImportYouthService>
                {
                    new()
                    {
                        Name = "Teen Sitter",
                        Services = new List<string> { "Babysit" },
                        ContactMethod = 1,
                        ParentNote = "Trusted by neighborhood",
                    },
                },
            }
        );

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
        vendorRepo.Verify(
            r => r.UpsertAsync(It.Is<Vendor>(v => v.Name == "Vendor A" && v.Notes == "Imported notes")),
            Times.Once
        );
        reviewRepo.Verify(
            r =>
                r.UpsertAsync(
                    It.Is<VendorReview>(v =>
                        v.AuthorDisplayName == "Referrer One"
                        && VendorReviewTimestamps.IsUnknown(v.CreatedUtc)
                        && VendorReviewTimestamps.IsUnknown(v.ModifiedUtc)
                    )
                ),
            Times.Once
        );
        youthRepo.Verify(r => r.UpsertAsync(It.Is<YouthServiceListing>(y => y.Name == "Teen Sitter")), Times.Once);
        auditRepo.Verify(r => r.AddAsync(It.IsAny<NewAuditLogEntry>()), Times.Once);
    }

    [Fact]
    public async Task ImportVendorsAndReviews_writes_initial_review_when_initial_review_text_set()
    {
        var userRepo = new Mock<IUserRepository>(MockBehavior.Strict);
        var vendorRepo = new Mock<IVendorRepository>(MockBehavior.Strict);
        var reviewRepo = new Mock<IVendorReviewRepository>(MockBehavior.Strict);
        var youthRepo = new Mock<IYouthServiceListingRepository>(MockBehavior.Strict);
        var auditRepo = new Mock<IAuditLogRepository>(MockBehavior.Strict);

        userRepo
            .Setup(r => r.GetByUniqueIdAsync("idpuser-1"))
            .ReturnsAsync(
                new User
                {
                    UniqueId = "idpuser-1",
                    GivenName = "Admin",
                    Surname = "User",
                    Roles = new List<User.Role> { User.Role.Administrator },
                }
            );

        vendorRepo.Setup(r => r.UpsertAsync(It.IsAny<Vendor>())).ReturnsAsync((Vendor v) => v);
        reviewRepo.Setup(r => r.UpsertAsync(It.IsAny<VendorReview>())).ReturnsAsync((VendorReview r) => r);
        auditRepo.Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var controller = BuildController(
            userRepo.Object,
            vendorRepo.Object,
            reviewRepo.Object,
            youthRepo.Object,
            auditRepo.Object
        );
        var result = await controller.ImportVendorsAndReviews(
            new VendorSeedImportRequest
            {
                Vendors = new List<VendorSeedImportVendor>
                {
                    new()
                    {
                        Fingerprint = "fp-1",
                        Name = "Vendor With Seed Review",
                        InitialReviewText = "Seed import opening review.",
                    },
                },
                Reviews = new List<VendorSeedImportReview>(),
            }
        );

        Assert.IsType<OkObjectResult>(result);
        reviewRepo.Verify(
            r =>
                r.UpsertAsync(
                    It.Is<VendorReview>(v =>
                        v.ReviewText == "Seed import opening review."
                        && v.AuthorUniqueId == "idpuser-1"
                        && !VendorReviewTimestamps.IsUnknown(v.CreatedUtc)
                    )
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task ImportVendorsAndReviews_skips_youth_when_born_year_out_of_range()
    {
        var userRepo = new Mock<IUserRepository>(MockBehavior.Strict);
        var vendorRepo = new Mock<IVendorRepository>(MockBehavior.Strict);
        var reviewRepo = new Mock<IVendorReviewRepository>(MockBehavior.Strict);
        var youthRepo = new Mock<IYouthServiceListingRepository>(MockBehavior.Strict);
        var auditRepo = new Mock<IAuditLogRepository>(MockBehavior.Strict);

        userRepo
            .Setup(r => r.GetByUniqueIdAsync("idpuser-1"))
            .ReturnsAsync(
                new User
                {
                    UniqueId = "idpuser-1",
                    GivenName = "Admin",
                    Surname = "User",
                    Roles = new List<User.Role> { User.Role.Administrator },
                }
            );

        vendorRepo.Setup(r => r.UpsertAsync(It.IsAny<Vendor>())).ReturnsAsync((Vendor v) => v);
        auditRepo.Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var controller = BuildController(
            userRepo.Object,
            vendorRepo.Object,
            reviewRepo.Object,
            youthRepo.Object,
            auditRepo.Object
        );
        var result = await controller.ImportVendorsAndReviews(
            new VendorSeedImportRequest
            {
                Vendors = new List<VendorSeedImportVendor>
                {
                    new() { Fingerprint = "only-v", Name = "Vendor Only" },
                },
                YouthServices = new List<VendorSeedImportYouthService>
                {
                    new()
                    {
                        Name = "Bad year",
                        Services = new List<string> { "Sit" },
                        BornYear = 1800,
                        ContactMethod = 1,
                    },
                },
            }
        );

        Assert.IsType<OkObjectResult>(result);
        youthRepo.Verify(r => r.UpsertAsync(It.IsAny<YouthServiceListing>()), Times.Never);
    }

    [Fact]
    public async Task ImportVendorsAndReviews_skips_duplicate_vendor_fingerprints()
    {
        var userRepo = new Mock<IUserRepository>(MockBehavior.Strict);
        var vendorRepo = new Mock<IVendorRepository>(MockBehavior.Strict);
        var reviewRepo = new Mock<IVendorReviewRepository>(MockBehavior.Strict);
        var youthRepo = new Mock<IYouthServiceListingRepository>(MockBehavior.Strict);
        var auditRepo = new Mock<IAuditLogRepository>(MockBehavior.Strict);

        userRepo
            .Setup(r => r.GetByUniqueIdAsync("idpuser-1"))
            .ReturnsAsync(
                new User
                {
                    UniqueId = "idpuser-1",
                    GivenName = "Admin",
                    Surname = "User",
                    Roles = new List<User.Role> { User.Role.Administrator },
                }
            );

        vendorRepo.Setup(r => r.UpsertAsync(It.IsAny<Vendor>())).ReturnsAsync((Vendor v) => v);
        reviewRepo.Setup(r => r.UpsertAsync(It.IsAny<VendorReview>())).ReturnsAsync((VendorReview r) => r);
        auditRepo.Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var controller = BuildController(
            userRepo.Object,
            vendorRepo.Object,
            reviewRepo.Object,
            youthRepo.Object,
            auditRepo.Object
        );
        var result = await controller.ImportVendorsAndReviews(
            new VendorSeedImportRequest
            {
                Vendors = new List<VendorSeedImportVendor>
                {
                    new() { Fingerprint = "dup-1", Name = "Vendor A" },
                    new() { Fingerprint = "dup-1", Name = "Vendor A Duplicate" },
                },
                Reviews = new List<VendorSeedImportReview>
                {
                    new()
                    {
                        VendorFingerprint = "dup-1",
                        AuthorDisplayName = "Referrer",
                        ReviewText = "Great.",
                    },
                },
            }
        );

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
        vendorRepo.Verify(r => r.UpsertAsync(It.IsAny<Vendor>()), Times.Once);
        reviewRepo.Verify(r => r.UpsertAsync(It.IsAny<VendorReview>()), Times.Once);
    }

    private static VendorImportController BuildController(
        IUserRepository userRepository,
        IVendorRepository vendorRepository,
        IVendorReviewRepository reviewRepository,
        IYouthServiceListingRepository youthServiceListingRepository,
        IAuditLogRepository auditLogRepository
    )
    {
        var controller = new VendorImportController(
            vendorRepository,
            reviewRepository,
            youthServiceListingRepository,
            new CurrentUserAccessor(userRepository),
            auditLogRepository
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
