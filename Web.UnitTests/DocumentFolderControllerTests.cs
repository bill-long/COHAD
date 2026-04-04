using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Moq;
using Web.Configuration;
using Web.Controllers;
using Web.Models;
using Web.PresentationModels;
using Web.Services;
using Web.Services.Repositories;
using Web.UpdateModels;

namespace Web.UnitTests;

public sealed class DocumentFolderControllerTests
{
    private const string IdentityProviderClaim = "http://schemas.microsoft.com/identity/claims/identityprovider";

    private static DocumentFolderController CreateController(
        IUserRepository users,
        IDocumentFolderRepository folders,
        IDocumentRepository documents,
        IAuditLogRepository auditLog,
        string nameId = "u1",
        string idp = "google.com")
    {
        var cache = new DocumentListCache(documents, folders, new MemoryCache(new MemoryCacheOptions()));
        var c = new DocumentFolderController(users, folders, documents, auditLog, cache);
        c.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, nameId),
                    new Claim(IdentityProviderClaim, idp)
                }, "Test"))
            }
        };
        return c;
    }

    private static string UniqueId(string nameId, string idp = "google.com") => $"{idp}{nameId}";

    private static User AdminUser(string nameId = "u1", string idp = "google.com") => new()
    {
        UniqueId = UniqueId(nameId, idp),
        GivenName = "Admin",
        Surname = "User",
        Roles = new List<User.Role> { User.Role.Administrator }
    };

    private static User ResidentUser(string nameId = "u1", string idp = "google.com") => new()
    {
        UniqueId = UniqueId(nameId, idp),
        Roles = new List<User.Role> { User.Role.Resident }
    };

    [Fact]
    public async Task Get_returns_Forbid_when_user_has_no_resident_or_admin_role()
    {
        var uniqueId = UniqueId("u1");
        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(new User
        {
            UniqueId = uniqueId,
            Roles = new List<User.Role> { User.Role.GardenClub }
        });

        var c = CreateController(mockUsers.Object, Mock.Of<IDocumentFolderRepository>(),
            Mock.Of<IDocumentRepository>(), Mock.Of<IAuditLogRepository>());

        var result = await c.Get();

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task Get_returns_folders_sorted_by_sort_order_with_document_counts()
    {
        var uniqueId = UniqueId("u1");
        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(ResidentUser());

        var folder1Id = Guid.NewGuid();
        var folder2Id = Guid.NewGuid();
        var folders = new List<DocumentFolder>
        {
            new() { Id = folder2Id, Name = "Zebra", SortOrder = 1 },
            new() { Id = folder1Id, Name = "Alpha", SortOrder = 0 }
        };
        var docs = new List<ResidentDocument>
        {
            new() { Id = Guid.NewGuid(), FolderId = folder1Id },
            new() { Id = Guid.NewGuid(), FolderId = folder1Id },
            new() { Id = Guid.NewGuid(), FolderId = folder2Id },
            new() { Id = Guid.NewGuid(), FolderId = null }
        };

        var mockFolders = new Mock<IDocumentFolderRepository>();
        mockFolders.Setup(r => r.GetAllAsync()).ReturnsAsync(folders);
        var mockDocs = new Mock<IDocumentRepository>();
        mockDocs.Setup(r => r.GetAllAsync()).ReturnsAsync(docs);

        var c = CreateController(mockUsers.Object, mockFolders.Object, mockDocs.Object, Mock.Of<IAuditLogRepository>());

        var result = await c.Get();

        var ok = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/json", ok.ContentType);
        using var doc = System.Text.Json.JsonDocument.Parse(ok.FileContents);
        var arr = doc.RootElement;
        Assert.Equal(System.Text.Json.JsonValueKind.Array, arr.ValueKind);
        Assert.Equal(2, arr.GetArrayLength());
        Assert.Equal("Alpha", arr[0].GetProperty("Name").GetString());
        Assert.Equal(2, arr[0].GetProperty("DocumentCount").GetInt32());
        Assert.Equal("Zebra", arr[1].GetProperty("Name").GetString());
        Assert.Equal(1, arr[1].GetProperty("DocumentCount").GetInt32());
    }

    [Fact]
    public async Task Create_returns_BadRequest_when_name_is_empty()
    {
        var uniqueId = UniqueId("u1");
        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(AdminUser());

        var c = CreateController(mockUsers.Object, Mock.Of<IDocumentFolderRepository>(),
            Mock.Of<IDocumentRepository>(), Mock.Of<IAuditLogRepository>());

        var result = await c.Create(new CreateFolderRequest { Name = "" });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Create_returns_Conflict_when_name_already_exists()
    {
        var uniqueId = UniqueId("u1");
        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(AdminUser());

        var existing = new List<DocumentFolder>
        {
            new() { Id = Guid.NewGuid(), Name = "Meeting Minutes", SortOrder = 0 }
        };
        var mockFolders = new Mock<IDocumentFolderRepository>();
        mockFolders.Setup(r => r.GetAllAsync()).ReturnsAsync(existing);

        var c = CreateController(mockUsers.Object, mockFolders.Object,
            Mock.Of<IDocumentRepository>(), Mock.Of<IAuditLogRepository>());

        var result = await c.Create(new CreateFolderRequest { Name = "meeting minutes" });

        Assert.IsType<ConflictObjectResult>(result);
    }

    [Fact]
    public async Task Create_succeeds_and_assigns_next_sort_order()
    {
        var uniqueId = UniqueId("u1");
        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(AdminUser());

        var existing = new List<DocumentFolder>
        {
            new() { Id = Guid.NewGuid(), Name = "Existing", SortOrder = 5 }
        };
        DocumentFolder saved = null;
        var mockFolders = new Mock<IDocumentFolderRepository>();
        mockFolders.Setup(r => r.GetAllAsync()).ReturnsAsync(existing);
        mockFolders.Setup(r => r.UpsertAsync(It.IsAny<DocumentFolder>()))
            .Callback<DocumentFolder>(f => saved = f)
            .ReturnsAsync((DocumentFolder f) => f);

        var c = CreateController(mockUsers.Object, mockFolders.Object,
            Mock.Of<IDocumentRepository>(), Mock.Of<IAuditLogRepository>());

        var result = await c.Create(new CreateFolderRequest { Name = "New Folder" });

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(saved);
        Assert.Equal("New Folder", saved.Name);
        Assert.Equal(6, saved.SortOrder);
    }

    [Fact]
    public async Task Delete_returns_Conflict_when_folder_has_documents()
    {
        var folderId = Guid.NewGuid();
        var uniqueId = UniqueId("u1");
        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(AdminUser());

        var mockFolders = new Mock<IDocumentFolderRepository>();
        mockFolders.Setup(r => r.GetByIdAsync(folderId))
            .ReturnsAsync(new DocumentFolder { Id = folderId, Name = "Test" });

        var docs = new List<ResidentDocument>
        {
            new() { Id = Guid.NewGuid(), FolderId = folderId }
        };
        var mockDocs = new Mock<IDocumentRepository>();
        mockDocs.Setup(r => r.GetAllAsync()).ReturnsAsync(docs);

        var c = CreateController(mockUsers.Object, mockFolders.Object, mockDocs.Object, Mock.Of<IAuditLogRepository>());

        var result = await c.Delete(folderId);

        Assert.IsType<ConflictObjectResult>(result);
    }

    [Fact]
    public async Task Delete_succeeds_when_folder_is_empty()
    {
        var folderId = Guid.NewGuid();
        var uniqueId = UniqueId("u1");
        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(AdminUser());

        var mockFolders = new Mock<IDocumentFolderRepository>();
        mockFolders.Setup(r => r.GetByIdAsync(folderId))
            .ReturnsAsync(new DocumentFolder { Id = folderId, Name = "Empty Folder" });

        var mockDocs = new Mock<IDocumentRepository>();
        mockDocs.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<ResidentDocument>());

        var c = CreateController(mockUsers.Object, mockFolders.Object, mockDocs.Object, Mock.Of<IAuditLogRepository>());

        var result = await c.Delete(folderId);

        Assert.IsType<OkResult>(result);
        mockFolders.Verify(r => r.DeleteAsync(folderId), Times.Once);
    }
}
