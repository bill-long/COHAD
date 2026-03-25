using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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

public sealed class BlogControllerTests
{
    private const string IdentityProviderClaim = "http://schemas.microsoft.com/identity/claims/identityprovider";

    private static BlogController CreateController(
        IUserRepository users,
        IBlogPostRepository posts,
        IBlogCommentRepository comments,
        IDocumentFileStore fileStore,
        IAuditLogRepository auditLog,
        DocumentStorageOptions? storageOptions = null,
        string nameId = "u1",
        string idp = "google.com")
    {
        storageOptions ??= new DocumentStorageOptions { MaxUploadBytes = 1024 * 1024 };

        var c = new BlogController(
            users,
            posts,
            comments,
            fileStore,
            auditLog,
            Options.Create(storageOptions));

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

    [Fact]
    public async Task GetManage_returns_Forbid_when_user_is_only_resident()
    {
        var uniqueId = UniqueId("u1");
        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(new User
        {
            UniqueId = uniqueId,
            Roles = new List<User.Role> { User.Role.Resident }
        });

        var c = CreateController(mockUsers.Object, Mock.Of<IBlogPostRepository>(), Mock.Of<IBlogCommentRepository>(),
            Mock.Of<IDocumentFileStore>(), Mock.Of<IAuditLogRepository>());
        var result = await c.GetManage();

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task GetManage_returns_posts_when_user_has_resident_plus_another_role()
    {
        var uniqueId = UniqueId("u1");
        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(new User
        {
            UniqueId = uniqueId,
            Roles = new List<User.Role> { User.Role.Resident, User.Role.Administrator }
        });

        var postId = Guid.NewGuid();
        var mockPosts = new Mock<IBlogPostRepository>();
        mockPosts.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<BlogPost>
        {
            new()
            {
                Id = postId,
                PublicSlug = "2026-test",
                Title = "Hello",
                Content = "x",
                Excerpt = "e",
                PublishUtc = DateTime.UtcNow,
                AuthorDisplayName = "A"
            }
        });

        var c = CreateController(mockUsers.Object, mockPosts.Object, Mock.Of<IBlogCommentRepository>(),
            Mock.Of<IDocumentFileStore>(), Mock.Of<IAuditLogRepository>());
        var result = await c.GetManage();

        var ok = Assert.IsType<OkObjectResult>(result);
        var list = Assert.IsAssignableFrom<List<BlogPostDetail>>(ok.Value);
        Assert.Single(list);
    }

    [Fact]
    public async Task UpsertManage_update_returns_409_when_cosmos_precondition_failed()
    {
        var uniqueId = UniqueId("u1");
        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(new User
        {
            UniqueId = uniqueId,
            Roles = new List<User.Role> { User.Role.Resident, User.Role.Administrator },
            GivenName = "Test",
            Surname = "User"
        });

        var id = Guid.NewGuid();
        var existing = new BlogPost
        {
            Id = id,
            PublicSlug = "2026-old",
            Title = "Old",
            Content = "body",
            Excerpt = "ex",
            PublishUtc = DateTime.UtcNow,
            AuthorDisplayName = "Test User"
        };

        var mockPosts = new Mock<IBlogPostRepository>();
        mockPosts.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<BlogPost> { existing });
        mockPosts.Setup(r => r.ReadAsync(id)).ReturnsAsync(new BlogPostReadResult
        {
            Post = existing,
            ETag = "\"etag\""
        });
        mockPosts
            .Setup(r => r.ReplaceAsync(It.IsAny<BlogPost>(), It.IsAny<string>()))
            .ThrowsAsync(new Microsoft.Azure.Cosmos.CosmosException(
                "conflict", HttpStatusCode.PreconditionFailed, 0, string.Empty, 0));

        var c = CreateController(mockUsers.Object, mockPosts.Object, Mock.Of<IBlogCommentRepository>(),
            Mock.Of<IDocumentFileStore>(), Mock.Of<IAuditLogRepository>());

        var request = new BlogPostUpsertRequest
        {
            Id = id,
            Title = "New title",
            Content = "New content that is long enough for excerpt.",
            Excerpt = "manual",
            PublishUtc = DateTime.UtcNow
        };

        var result = await c.UpsertManage(request);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, status.StatusCode);
    }

    [Fact]
    public async Task DeleteManage_deletes_inline_upload_blobs_referenced_in_markdown()
    {
        var uniqueId = UniqueId("u1");
        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(new User
        {
            UniqueId = uniqueId,
            Roles = new List<User.Role> { User.Role.Resident, User.Role.Administrator },
            GivenName = "Test",
            Surname = "User"
        });

        var postId = Guid.NewGuid();
        var inlinePath = "blog/images/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee/photo.jpg";
        var stored = new BlogPost
        {
            Id = postId,
            Title = "T",
            Content = $"![](/api/blog/images/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee/photo.jpg)",
            PublicSlug = "2026-x",
            PublishUtc = DateTime.UtcNow,
            AuthorDisplayName = "A"
        };

        var mockPosts = new Mock<IBlogPostRepository>();
        mockPosts.Setup(r => r.GetByIdAsync(postId)).ReturnsAsync(stored);
        mockPosts.Setup(r => r.DeleteAsync(postId)).Returns(Task.CompletedTask);

        var mockComments = new Mock<IBlogCommentRepository>();
        mockComments.Setup(r => r.DeleteByBlogPostCascadeAsync(postId)).Returns(Task.CompletedTask);

        var deleted = new List<string>();
        var mockFiles = new Mock<IDocumentFileStore>();
        mockFiles.Setup(f => f.DeleteAsync(It.IsAny<string>()))
            .Returns(Task.CompletedTask)
            .Callback<string>(deleted.Add);

        var mockAudit = new Mock<IAuditLogRepository>();
        mockAudit.Setup(a => a.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var c = CreateController(mockUsers.Object, mockPosts.Object, mockComments.Object, mockFiles.Object, mockAudit.Object);
        var result = await c.DeleteManage(postId);

        Assert.IsType<OkResult>(result);
        Assert.Contains(inlinePath, deleted);
    }
}
