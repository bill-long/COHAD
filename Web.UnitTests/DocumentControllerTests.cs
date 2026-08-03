using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Moq;
using Web.Configuration;
using Web.Controllers;
using Web.Models;
using Web.Services;
using Web.Services.Repositories;
using Web.UpdateModels;

namespace Web.UnitTests;

public sealed class DocumentControllerTests
{
    private const string IdentityProviderClaim = "http://schemas.microsoft.com/identity/claims/identityprovider";
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    private static DocumentController CreateController(
        IUserRepository users,
        IDocumentRepository documents,
        IDocumentFileStore fileStore,
        IAuditLogRepository auditLog,
        IDocumentFolderRepository folders = null,
        string nameId = "u1",
        string idp = "google.com"
    )
    {
        folders ??= Mock.Of<IDocumentFolderRepository>();
        var cache = new DocumentListCache(
            documents,
            folders,
            new MemoryCache(new MemoryCacheOptions()),
            WebJsonOptions
        );
        var c = new DocumentController(
            new CurrentUserAccessor(users),
            documents,
            folders,
            fileStore,
            auditLog,
            cache,
            Options.Create(new DocumentStorageOptions { MaxUploadBytes = 1024 * 1024 })
        );

        c.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(
                    new ClaimsIdentity(
                        new[] { new Claim(ClaimTypes.NameIdentifier, nameId), new Claim(IdentityProviderClaim, idp) },
                        "Test"
                    )
                ),
            },
        };
        return c;
    }

    private static string UniqueId(string nameId, string idp = "google.com") => $"{idp}{nameId}";

    [Fact]
    public async Task Get_returns_Forbid_when_user_has_no_resident_or_admin_role()
    {
        var uniqueId = UniqueId("u1");
        var mockUsers = new Mock<IUserRepository>();
        mockUsers
            .Setup(r => r.GetByUniqueIdAsync(uniqueId))
            .ReturnsAsync(
                new User
                {
                    UniqueId = uniqueId,
                    Roles = new List<User.Role> { User.Role.GardenClub },
                }
            );

        var c = CreateController(
            mockUsers.Object,
            Mock.Of<IDocumentRepository>(),
            Mock.Of<IDocumentFileStore>(),
            Mock.Of<IAuditLogRepository>()
        );

        var result = await c.Get();

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task Get_returns_documents_when_user_is_resident()
    {
        var uniqueId = UniqueId("u1");
        var now = DateTime.UtcNow;
        var docs = new List<ResidentDocument>
        {
            new()
            {
                Id = Guid.NewGuid(),
                DisplayName = "b.pdf",
                CreatedUtc = now.AddMinutes(-1),
            },
            new()
            {
                Id = Guid.NewGuid(),
                DisplayName = "a.pdf",
                CreatedUtc = now,
            },
        };

        var mockUsers = new Mock<IUserRepository>();
        mockUsers
            .Setup(r => r.GetByUniqueIdAsync(uniqueId))
            .ReturnsAsync(
                new User
                {
                    UniqueId = uniqueId,
                    Roles = new List<User.Role> { User.Role.Resident },
                }
            );
        var mockDocs = new Mock<IDocumentRepository>();
        mockDocs.Setup(r => r.GetAllAsync()).ReturnsAsync(docs);
        var mockFolders = new Mock<IDocumentFolderRepository>();
        mockFolders.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<DocumentFolder>());

        var c = CreateController(
            mockUsers.Object,
            mockDocs.Object,
            Mock.Of<IDocumentFileStore>(),
            Mock.Of<IAuditLogRepository>(),
            mockFolders.Object
        );

        var result = await c.Get();

        var ok = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/json", ok.ContentType);
        Assert.NotEmpty(ok.FileContents);
    }

    [Fact]
    public async Task Get_sets_CacheControl_and_ETag_headers()
    {
        var uniqueId = UniqueId("u1");
        var mockUsers = new Mock<IUserRepository>();
        mockUsers
            .Setup(r => r.GetByUniqueIdAsync(uniqueId))
            .ReturnsAsync(
                new User
                {
                    UniqueId = uniqueId,
                    Roles = new List<User.Role> { User.Role.Resident },
                }
            );
        var mockDocs = new Mock<IDocumentRepository>();
        mockDocs.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<ResidentDocument>());
        var mockFolders = new Mock<IDocumentFolderRepository>();
        mockFolders.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<DocumentFolder>());

        var c = CreateController(
            mockUsers.Object,
            mockDocs.Object,
            Mock.Of<IDocumentFileStore>(),
            Mock.Of<IAuditLogRepository>(),
            mockFolders.Object
        );
        await c.Get();

        Assert.Equal("private, no-cache", c.Response.Headers.CacheControl.ToString());
        Assert.False(string.IsNullOrEmpty(c.Response.Headers.ETag.ToString()));
    }

    [Fact]
    public async Task Get_returns_304_when_If_None_Match_matches()
    {
        var uniqueId = UniqueId("u1");
        var mockUsers = new Mock<IUserRepository>();
        mockUsers
            .Setup(r => r.GetByUniqueIdAsync(uniqueId))
            .ReturnsAsync(
                new User
                {
                    UniqueId = uniqueId,
                    Roles = new List<User.Role> { User.Role.Resident },
                }
            );
        var mockDocs = new Mock<IDocumentRepository>();
        mockDocs
            .Setup(r => r.GetAllAsync())
            .ReturnsAsync(
                new List<ResidentDocument>
                {
                    new()
                    {
                        Id = Guid.NewGuid(),
                        DisplayName = "a.pdf",
                        CreatedUtc = DateTime.UtcNow,
                    },
                }
            );
        var mockFolders = new Mock<IDocumentFolderRepository>();
        mockFolders.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<DocumentFolder>());

        // First call: get the ETag
        var c1 = CreateController(
            mockUsers.Object,
            mockDocs.Object,
            Mock.Of<IDocumentFileStore>(),
            Mock.Of<IAuditLogRepository>(),
            mockFolders.Object
        );
        await c1.Get();
        var etag = c1.Response.Headers.ETag.ToString();

        // Second call: send If-None-Match with the same ETag
        var c2 = CreateController(
            mockUsers.Object,
            mockDocs.Object,
            Mock.Of<IDocumentFileStore>(),
            Mock.Of<IAuditLogRepository>(),
            mockFolders.Object
        );
        c2.Request.Headers["If-None-Match"] = etag;
        var result = await c2.Get();

        var status = Assert.IsType<StatusCodeResult>(result);
        Assert.Equal(StatusCodes.Status304NotModified, status.StatusCode);
    }

    [Fact]
    public async Task Download_returns_NotFound_when_document_does_not_exist()
    {
        var uniqueId = UniqueId("u1");
        var mockUsers = new Mock<IUserRepository>();
        mockUsers
            .Setup(r => r.GetByUniqueIdAsync(uniqueId))
            .ReturnsAsync(
                new User
                {
                    UniqueId = uniqueId,
                    Roles = new List<User.Role> { User.Role.Administrator },
                }
            );
        var mockDocs = new Mock<IDocumentRepository>();
        mockDocs.Setup(r => r.GetByIdAsync(It.IsAny<Guid>())).ReturnsAsync((ResidentDocument)null);

        var c = CreateController(
            mockUsers.Object,
            mockDocs.Object,
            Mock.Of<IDocumentFileStore>(),
            Mock.Of<IAuditLogRepository>()
        );

        var result = await c.Download(Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Upload_returns_BadRequest_for_unsupported_extension()
    {
        var uniqueId = UniqueId("u1");
        var mockUsers = new Mock<IUserRepository>();
        mockUsers
            .Setup(r => r.GetByUniqueIdAsync(uniqueId))
            .ReturnsAsync(
                new User
                {
                    UniqueId = uniqueId,
                    Roles = new List<User.Role> { User.Role.Administrator },
                }
            );

        var c = CreateController(
            mockUsers.Object,
            Mock.Of<IDocumentRepository>(),
            Mock.Of<IDocumentFileStore>(),
            Mock.Of<IAuditLogRepository>()
        );

        var request = new DocumentUploadRequest
        {
            DisplayName = "test",
            File = CreateFormFile("test.exe", "application/octet-stream", Encoding.UTF8.GetBytes("x")),
        };
        var result = await c.Upload(request);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Upload_saves_file_and_metadata_when_valid()
    {
        var uniqueId = UniqueId("u1");
        var mockUsers = new Mock<IUserRepository>();
        mockUsers
            .Setup(r => r.GetByUniqueIdAsync(uniqueId))
            .ReturnsAsync(
                new User
                {
                    UniqueId = uniqueId,
                    GivenName = "Admin",
                    Surname = "User",
                    Roles = new List<User.Role> { User.Role.Administrator },
                }
            );

        ResidentDocument saved = null;
        var mockDocs = new Mock<IDocumentRepository>();
        mockDocs
            .Setup(r => r.UpsertAsync(It.IsAny<ResidentDocument>()))
            .Callback<ResidentDocument>(d => saved = d)
            .ReturnsAsync((ResidentDocument d) => d);

        var mockFileStore = new Mock<IDocumentFileStore>();
        mockFileStore
            .Setup(s => s.UploadAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        var c = CreateController(
            mockUsers.Object,
            mockDocs.Object,
            mockFileStore.Object,
            Mock.Of<IAuditLogRepository>()
        );
        var request = new DocumentUploadRequest
        {
            DisplayName = "Directory",
            File = CreateFormFile("directory.pdf", "application/pdf", Encoding.UTF8.GetBytes("pdf")),
        };

        var result = await c.Upload(request);

        Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(saved);
        Assert.EndsWith(".pdf", saved.DisplayName, StringComparison.OrdinalIgnoreCase);
        mockFileStore.Verify(s => s.UploadAsync(It.IsAny<string>(), It.IsAny<Stream>(), "application/pdf"), Times.Once);
    }

    [Fact]
    public async Task Upload_does_not_duplicate_extension_when_display_name_includes_extension()
    {
        var uniqueId = UniqueId("u1");
        var mockUsers = new Mock<IUserRepository>();
        mockUsers
            .Setup(r => r.GetByUniqueIdAsync(uniqueId))
            .ReturnsAsync(
                new User
                {
                    UniqueId = uniqueId,
                    GivenName = "Admin",
                    Surname = "User",
                    Roles = new List<User.Role> { User.Role.Administrator },
                }
            );

        ResidentDocument saved = null;
        var mockDocs = new Mock<IDocumentRepository>();
        mockDocs
            .Setup(r => r.UpsertAsync(It.IsAny<ResidentDocument>()))
            .Callback<ResidentDocument>(d => saved = d)
            .ReturnsAsync((ResidentDocument d) => d);

        var mockFileStore = new Mock<IDocumentFileStore>();
        mockFileStore
            .Setup(s => s.UploadAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        var c = CreateController(
            mockUsers.Object,
            mockDocs.Object,
            mockFileStore.Object,
            Mock.Of<IAuditLogRepository>()
        );
        var request = new DocumentUploadRequest
        {
            DisplayName = "Rules.pdf",
            File = CreateFormFile("anything.pdf", "application/pdf", Encoding.UTF8.GetBytes("pdf")),
        };

        var result = await c.Upload(request);

        Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(saved);
        Assert.Equal("Rules.pdf", saved.DisplayName);
    }

    private static IFormFile CreateFormFile(string fileName, string contentType, byte[] bytes)
    {
        var ms = new MemoryStream(bytes);
        return new FormFile(ms, 0, ms.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType,
        };
    }
}
