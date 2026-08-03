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
using Web.PresentationModels;
using Web.Services.Repositories;
using Web.UpdateModels;

namespace Web.UnitTests;

public sealed class YouthServicesControllerTests
{
    [Fact]
    public async Task GetAll_returns_listings_sorted_by_surname_then_full_name()
    {
        var userRepo = new Mock<IUserRepository>(MockBehavior.Strict);
        var listingRepo = new Mock<IYouthServiceListingRepository>(MockBehavior.Strict);
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

        listingRepo
            .Setup(r => r.GetAllAsync())
            .ReturnsAsync(
                new List<YouthServiceListing>
                {
                    new()
                    {
                        Id = Guid.NewGuid(),
                        Name = "Zara Adams",
                        CreatedByUniqueId = "other",
                    },
                    new()
                    {
                        Id = Guid.NewGuid(),
                        Name = "Amy carter",
                        CreatedByUniqueId = "other",
                    },
                    new()
                    {
                        Id = Guid.NewGuid(),
                        Name = "Beth Carter",
                        CreatedByUniqueId = "other",
                    },
                    new()
                    {
                        Id = Guid.NewGuid(),
                        Name = "Jake Adams",
                        CreatedByUniqueId = "other",
                    },
                    new()
                    {
                        Id = Guid.NewGuid(),
                        Name = "Solo",
                        CreatedByUniqueId = "other",
                    },
                }
            );

        var controller = BuildController(userRepo.Object, listingRepo.Object, auditRepo.Object);
        var result = await controller.GetAll();

        var ok = Assert.IsType<OkObjectResult>(result);
        var items = Assert.IsType<List<YouthServiceListingPresentation>>(ok.Value);
        var names = items.ConvertAll(i => i.Name);
        Assert.Equal(new List<string> { "Jake Adams", "Zara Adams", "Amy carter", "Beth Carter", "Solo" }, names);
    }

    [Fact]
    public async Task Create_returns_bad_request_when_name_missing()
    {
        var userRepo = new Mock<IUserRepository>(MockBehavior.Strict);
        var listingRepo = new Mock<IYouthServiceListingRepository>(MockBehavior.Strict);
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

        var controller = BuildController(userRepo.Object, listingRepo.Object, auditRepo.Object);
        var result = await controller.Create(new YouthServiceUpsertRequest { Name = " " });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Create_persists_listing_and_returns_ok()
    {
        var userRepo = new Mock<IUserRepository>(MockBehavior.Strict);
        var listingRepo = new Mock<IYouthServiceListingRepository>(MockBehavior.Strict);
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
        listingRepo
            .Setup(r => r.UpsertAsync(It.IsAny<YouthServiceListing>()))
            .ReturnsAsync((YouthServiceListing l) => l);
        auditRepo.Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var controller = BuildController(userRepo.Object, listingRepo.Object, auditRepo.Object);
        var result = await controller.Create(
            new YouthServiceUpsertRequest
            {
                Name = "Sam Sitter",
                Services = new List<string> { "Babysit" },
                ContactMethod = PreferredContactMethod.Text,
            }
        );

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
        listingRepo.Verify(
            r =>
                r.UpsertAsync(
                    It.Is<YouthServiceListing>(l => l.Name == "Sam Sitter" && l.CreatedByUniqueId == "idpuser-1")
                ),
            Times.Once
        );
        auditRepo.Verify(r => r.AddAsync(It.IsAny<NewAuditLogEntry>()), Times.Once);
    }

    [Fact]
    public async Task Create_returns_bad_request_when_born_year_invalid()
    {
        var userRepo = new Mock<IUserRepository>(MockBehavior.Strict);
        var listingRepo = new Mock<IYouthServiceListingRepository>(MockBehavior.Strict);
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

        var controller = BuildController(userRepo.Object, listingRepo.Object, auditRepo.Object);
        var result = await controller.Create(new YouthServiceUpsertRequest { Name = "Sam Sitter", BornYear = 1800 });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Update_forbids_non_creator_non_admin()
    {
        var userRepo = new Mock<IUserRepository>(MockBehavior.Strict);
        var listingRepo = new Mock<IYouthServiceListingRepository>(MockBehavior.Strict);
        var auditRepo = new Mock<IAuditLogRepository>(MockBehavior.Strict);
        var listingId = Guid.NewGuid();

        userRepo
            .Setup(r => r.GetByUniqueIdAsync("idpuser-1"))
            .ReturnsAsync(
                new User
                {
                    UniqueId = "idpuser-1",
                    Roles = new List<User.Role> { User.Role.Resident },
                }
            );
        listingRepo
            .Setup(r => r.GetByIdAsync(listingId))
            .ReturnsAsync(
                new YouthServiceListing
                {
                    Id = listingId,
                    Name = "Teen Tutor",
                    CreatedByUniqueId = "someone-else",
                }
            );

        var controller = BuildController(userRepo.Object, listingRepo.Object, auditRepo.Object);
        var result = await controller.Update(listingId, new YouthServiceUpsertRequest { Name = "Updated" });

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task Update_allows_creator_and_persists_changes()
    {
        var userRepo = new Mock<IUserRepository>(MockBehavior.Strict);
        var listingRepo = new Mock<IYouthServiceListingRepository>(MockBehavior.Strict);
        var auditRepo = new Mock<IAuditLogRepository>(MockBehavior.Strict);
        var listingId = Guid.NewGuid();

        userRepo
            .Setup(r => r.GetByUniqueIdAsync("idpuser-1"))
            .ReturnsAsync(
                new User
                {
                    UniqueId = "idpuser-1",
                    Roles = new List<User.Role> { User.Role.Resident },
                }
            );
        listingRepo
            .Setup(r => r.GetByIdAsync(listingId))
            .ReturnsAsync(
                new YouthServiceListing
                {
                    Id = listingId,
                    Name = "Old Name",
                    CreatedByUniqueId = "idpuser-1",
                }
            );
        listingRepo
            .Setup(r => r.UpsertAsync(It.IsAny<YouthServiceListing>()))
            .ReturnsAsync((YouthServiceListing l) => l);
        auditRepo.Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var controller = BuildController(userRepo.Object, listingRepo.Object, auditRepo.Object);
        var result = await controller.Update(listingId, new YouthServiceUpsertRequest { Name = "New Name" });

        Assert.IsType<OkObjectResult>(result);
        listingRepo.Verify(
            r =>
                r.UpsertAsync(
                    It.Is<YouthServiceListing>(l =>
                        l.Id == listingId && l.Name == "New Name" && l.ModifiedByUniqueId == "idpuser-1"
                    )
                ),
            Times.Once
        );
        auditRepo.Verify(r => r.AddAsync(It.IsAny<NewAuditLogEntry>()), Times.Once);
    }

    [Fact]
    public async Task Update_allows_administrator_for_other_creator()
    {
        var userRepo = new Mock<IUserRepository>(MockBehavior.Strict);
        var listingRepo = new Mock<IYouthServiceListingRepository>(MockBehavior.Strict);
        var auditRepo = new Mock<IAuditLogRepository>(MockBehavior.Strict);
        var listingId = Guid.NewGuid();

        userRepo
            .Setup(r => r.GetByUniqueIdAsync("idpuser-1"))
            .ReturnsAsync(
                new User
                {
                    UniqueId = "idpuser-1",
                    Roles = new List<User.Role> { User.Role.Administrator },
                }
            );
        listingRepo
            .Setup(r => r.GetByIdAsync(listingId))
            .ReturnsAsync(
                new YouthServiceListing
                {
                    Id = listingId,
                    Name = "Old Name",
                    CreatedByUniqueId = "someone-else",
                }
            );
        listingRepo
            .Setup(r => r.UpsertAsync(It.IsAny<YouthServiceListing>()))
            .ReturnsAsync((YouthServiceListing l) => l);
        auditRepo.Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var controller = BuildController(userRepo.Object, listingRepo.Object, auditRepo.Object);
        var result = await controller.Update(listingId, new YouthServiceUpsertRequest { Name = "Admin Updated Name" });

        Assert.IsType<OkObjectResult>(result);
        listingRepo.Verify(
            r => r.UpsertAsync(It.Is<YouthServiceListing>(l => l.Id == listingId && l.Name == "Admin Updated Name")),
            Times.Once
        );
        auditRepo.Verify(r => r.AddAsync(It.IsAny<NewAuditLogEntry>()), Times.Once);
    }

    [Fact]
    public async Task Delete_allows_administrator()
    {
        var userRepo = new Mock<IUserRepository>(MockBehavior.Strict);
        var listingRepo = new Mock<IYouthServiceListingRepository>(MockBehavior.Strict);
        var auditRepo = new Mock<IAuditLogRepository>(MockBehavior.Strict);
        var listingId = Guid.NewGuid();

        userRepo
            .Setup(r => r.GetByUniqueIdAsync("idpuser-1"))
            .ReturnsAsync(
                new User
                {
                    UniqueId = "idpuser-1",
                    Roles = new List<User.Role> { User.Role.Administrator },
                }
            );
        listingRepo
            .Setup(r => r.GetByIdAsync(listingId))
            .ReturnsAsync(
                new YouthServiceListing
                {
                    Id = listingId,
                    Name = "Teen Tutor",
                    CreatedByUniqueId = "someone-else",
                }
            );
        listingRepo.Setup(r => r.DeleteAsync(listingId)).Returns(Task.CompletedTask);
        auditRepo.Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var controller = BuildController(userRepo.Object, listingRepo.Object, auditRepo.Object);
        var result = await controller.Delete(listingId);

        Assert.IsType<OkResult>(result);
        listingRepo.Verify(r => r.DeleteAsync(listingId), Times.Once);
        auditRepo.Verify(r => r.AddAsync(It.IsAny<NewAuditLogEntry>()), Times.Once);
    }

    [Fact]
    public async Task Delete_allows_creator()
    {
        var userRepo = new Mock<IUserRepository>(MockBehavior.Strict);
        var listingRepo = new Mock<IYouthServiceListingRepository>(MockBehavior.Strict);
        var auditRepo = new Mock<IAuditLogRepository>(MockBehavior.Strict);
        var listingId = Guid.NewGuid();

        userRepo
            .Setup(r => r.GetByUniqueIdAsync("idpuser-1"))
            .ReturnsAsync(
                new User
                {
                    UniqueId = "idpuser-1",
                    Roles = new List<User.Role> { User.Role.Resident },
                }
            );
        listingRepo
            .Setup(r => r.GetByIdAsync(listingId))
            .ReturnsAsync(
                new YouthServiceListing
                {
                    Id = listingId,
                    Name = "Teen Tutor",
                    CreatedByUniqueId = "idpuser-1",
                }
            );
        listingRepo.Setup(r => r.DeleteAsync(listingId)).Returns(Task.CompletedTask);
        auditRepo.Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var controller = BuildController(userRepo.Object, listingRepo.Object, auditRepo.Object);
        var result = await controller.Delete(listingId);

        Assert.IsType<OkResult>(result);
        listingRepo.Verify(r => r.DeleteAsync(listingId), Times.Once);
        auditRepo.Verify(r => r.AddAsync(It.IsAny<NewAuditLogEntry>()), Times.Once);
    }

    private static YouthServicesController BuildController(
        IUserRepository userRepository,
        IYouthServiceListingRepository youthServiceRepository,
        IAuditLogRepository auditLogRepository
    )
    {
        var controller = new YouthServicesController(youthServiceRepository, userRepository, new CurrentUserAccessor(userRepository), auditLogRepository)
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
