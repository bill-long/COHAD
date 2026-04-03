using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
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
        IImageUploadHelper? imageUploadHelper = null,
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
            imageUploadHelper ?? DefaultImageUploadHelper(),
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

    /// <summary>Returns a default IImageUploadHelper that passes through file extension/size unchanged.</summary>
    private static IImageUploadHelper DefaultImageUploadHelper()
    {
        var mock = new Mock<IImageUploadHelper>();
        mock.Setup(h => h.ConvertAndUploadAsync(It.IsAny<IFormFile>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((IFormFile f, string ext, string prefix, string baseName) =>
                new ImageUploadResult($"{prefix}/{baseName}{ext.ToLowerInvariant()}", $"{baseName}{ext.ToLowerInvariant()}",
                    ImageContentTypes.FromExtension(ext), f.Length));
        return mock.Object;
    }

    private static BlogController CreateAnonymousController(
        IBlogPostRepository posts,
        IBlogCommentRepository? comments = null,
        IDocumentFileStore? files = null)
    {
        return new BlogController(
            Mock.Of<IUserRepository>(),
            posts,
            comments ?? Mock.Of<IBlogCommentRepository>(),
            files ?? Mock.Of<IDocumentFileStore>(),
            Mock.Of<IAuditLogRepository>(),
            Mock.Of<IImageUploadHelper>(),
            Options.Create(new DocumentStorageOptions { MaxUploadBytes = 1024 * 1024 }))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    [Fact]
    public async Task GetPublished_returns_cards_from_repository()
    {
        var publishUtc = DateTime.UtcNow.AddDays(-1);
        var postId = Guid.NewGuid();
        var mockPosts = new Mock<IBlogPostRepository>();
        mockPosts.Setup(r => r.GetPublishedAsync(It.IsAny<DateTime>())).ReturnsAsync(new List<BlogPost>
        {
            new()
            {
                Id = postId,
                PublicSlug = "2026-public",
                Title = "Hello",
                Content = null,
                Excerpt = "e",
                PublishUtc = publishUtc,
                AuthorDisplayName = "Author"
            }
        });

        var c = CreateAnonymousController(mockPosts.Object);
        var result = await c.GetPublished();

        var ok = Assert.IsType<OkObjectResult>(result);
        var list = Assert.IsType<List<BlogPostCard>>(ok.Value);
        Assert.Single(list);
        Assert.Equal("Hello", list[0].Title);
    }

    [Fact]
    public async Task GetBySegment_returns_detail_when_post_exists()
    {
        var mockPosts = new Mock<IBlogPostRepository>();
        mockPosts.Setup(r => r.GetByRouteSegmentAsync("2026-test")).ReturnsAsync(new BlogPost
        {
            Id = Guid.NewGuid(),
            PublicSlug = "2026-test",
            Title = "T",
            Content = "## Body",
            Excerpt = "ex",
            PublishUtc = DateTime.UtcNow,
            AuthorDisplayName = "A"
        });

        var c = CreateAnonymousController(mockPosts.Object);
        var result = await c.GetBySegment("2026-test");

        var ok = Assert.IsType<OkObjectResult>(result);
        var detail = Assert.IsType<BlogPostDetail>(ok.Value);
        Assert.Equal("T", detail.Title);
    }

    [Fact]
    public async Task GetBySegment_returns_NotFound_when_missing()
    {
        var mockPosts = new Mock<IBlogPostRepository>();
        mockPosts.Setup(r => r.GetByRouteSegmentAsync("nope")).ReturnsAsync((BlogPost)null!);

        var c = CreateAnonymousController(mockPosts.Object);
        var result = await c.GetBySegment("nope");

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task DownloadFeaturedImage_returns_file_when_blob_exists()
    {
        var mockPosts = new Mock<IBlogPostRepository>();
        mockPosts.Setup(r => r.GetByRouteSegmentAsync("2026-x")).ReturnsAsync(new BlogPost
        {
            Id = Guid.NewGuid(),
            FeaturedImageBlobPath = "blog/guid/photo.jpg",
            PublicSlug = "2026-x",
            Title = "T",
            Content = "c",
            PublishUtc = DateTime.UtcNow,
            AuthorDisplayName = "A"
        });

        var mockFiles = new Mock<IDocumentFileStore>();
        mockFiles.Setup(f => f.DownloadAsync("blog/guid/photo.jpg")).ReturnsAsync(new DocumentFileResult
        {
            Stream = new MemoryStream(new byte[] { 1, 2, 3 }),
            ContentType = "image/jpeg"
        });

        var c = CreateAnonymousController(mockPosts.Object, files: mockFiles.Object);
        var result = await c.DownloadFeaturedImage("2026-x");

        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("image/jpeg", file.ContentType);
    }

    [Fact]
    public async Task GetComments_returns_ok_when_post_exists()
    {
        var postId = Guid.NewGuid();
        var mockPosts = new Mock<IBlogPostRepository>();
        mockPosts.Setup(r => r.GetByRouteSegmentAsync("2026-post")).ReturnsAsync(new BlogPost
        {
            Id = postId,
            PublicSlug = "2026-post",
            Title = "T",
            Content = "c",
            PublishUtc = DateTime.UtcNow,
            AuthorDisplayName = "A"
        });

        var mockComments = new Mock<IBlogCommentRepository>();
        mockComments.Setup(r => r.GetByBlogPostIdAsync(postId)).ReturnsAsync(new List<BlogComment>());

        var c = CreateAnonymousController(mockPosts.Object, mockComments.Object);
        var result = await c.GetComments("2026-post");

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsAssignableFrom<IReadOnlyList<BlogCommentPresentation>>(ok.Value);
        Assert.Empty(payload);
    }

    [Fact]
    public async Task GetComments_with_authenticated_user_missing_required_claims_does_not_throw()
    {
        var postId = Guid.NewGuid();
        var mockPosts = new Mock<IBlogPostRepository>();
        mockPosts.Setup(r => r.GetByRouteSegmentAsync("2026-post")).ReturnsAsync(new BlogPost
        {
            Id = postId,
            PublicSlug = "2026-post",
            Title = "T",
            Content = "c",
            PublishUtc = DateTime.UtcNow,
            AuthorDisplayName = "A"
        });

        var mockComments = new Mock<IBlogCommentRepository>();
        mockComments.Setup(r => r.GetByBlogPostIdAsync(postId)).ReturnsAsync(new List<BlogComment>());

        var c = CreateAnonymousController(mockPosts.Object, mockComments.Object);
        c.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.Name, "broken-user")
                }, "Test"))
            }
        };

        var result = await c.GetComments("2026-post");
        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsAssignableFrom<IReadOnlyList<BlogCommentPresentation>>(ok.Value);
        Assert.Empty(payload);
    }

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
        mockPosts.Setup(r => r.GetSlugCandidatesAsync()).ReturnsAsync(new List<BlogPost> { existing });
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
    public async Task UpsertManage_update_with_new_featured_image_conflict_deletes_new_blob_not_existing_blob()
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
            AuthorDisplayName = "Test User",
            FeaturedImageBlobPath = $"blog/{id:D}/old.jpg"
        };

        var mockPosts = new Mock<IBlogPostRepository>();
        mockPosts.Setup(r => r.GetSlugCandidatesAsync()).ReturnsAsync(new List<BlogPost> { existing });
        mockPosts.Setup(r => r.ReadAsync(id)).ReturnsAsync(new BlogPostReadResult
        {
            Post = existing,
            ETag = "\"etag\""
        });
        mockPosts
            .Setup(r => r.ReplaceAsync(It.IsAny<BlogPost>(), It.IsAny<string>()))
            .ThrowsAsync(new Microsoft.Azure.Cosmos.CosmosException(
                "conflict", HttpStatusCode.PreconditionFailed, 0, string.Empty, 0));

        var deleted = new List<string>();
        var mockFiles = new Mock<IDocumentFileStore>();
        mockFiles.Setup(f => f.UploadAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        mockFiles.Setup(f => f.DeleteAsync(It.IsAny<string>()))
            .Returns(Task.CompletedTask)
            .Callback<string>(deleted.Add);

        var c = CreateController(mockUsers.Object, mockPosts.Object, Mock.Of<IBlogCommentRepository>(),
            mockFiles.Object, Mock.Of<IAuditLogRepository>());

        var imgBytes = new byte[] { 1, 2, 3 };
        IFormFile formFile = new FormFile(new MemoryStream(imgBytes), 0, imgBytes.Length, "FeaturedImage", "new image.jpg")
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/jpeg"
        };

        var request = new BlogPostUpsertRequest
        {
            Id = id,
            Title = "New title",
            Content = "New content that is long enough for excerpt.",
            Excerpt = "manual",
            PublishUtc = DateTime.UtcNow,
            FeaturedImage = formFile
        };

        var result = await c.UpsertManage(request);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, status.StatusCode);
        Assert.Contains(deleted, p => p.StartsWith($"blog/{id:D}/", StringComparison.OrdinalIgnoreCase) && p.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) && !p.EndsWith("/old.jpg", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain($"blog/{id:D}/old.jpg", deleted);
    }

    [Fact]
    public async Task UpsertManage_create_with_upload_and_save_exception_deletes_uploaded_blob()
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

        var mockPosts = new Mock<IBlogPostRepository>();
        mockPosts.Setup(r => r.GetSlugCandidatesAsync()).ReturnsAsync(new List<BlogPost>());
        mockPosts.Setup(r => r.UpsertAsync(It.IsAny<BlogPost>())).ThrowsAsync(new InvalidOperationException("save failed"));

        var deleted = new List<string>();
        var mockFiles = new Mock<IDocumentFileStore>();
        mockFiles.Setup(f => f.UploadAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        mockFiles.Setup(f => f.DeleteAsync(It.IsAny<string>()))
            .Returns(Task.CompletedTask)
            .Callback<string>(deleted.Add);

        var c = CreateController(mockUsers.Object, mockPosts.Object, Mock.Of<IBlogCommentRepository>(),
            mockFiles.Object, Mock.Of<IAuditLogRepository>());

        var imgBytes = new byte[] { 1, 2, 3 };
        IFormFile formFile = new FormFile(new MemoryStream(imgBytes), 0, imgBytes.Length, "FeaturedImage", "new-image.jpg")
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/jpeg"
        };

        var request = new BlogPostUpsertRequest
        {
            Title = "New title",
            Content = "New content that is long enough for excerpt.",
            Excerpt = "manual",
            PublishUtc = DateTime.UtcNow,
            FeaturedImage = formFile
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => c.UpsertManage(request));
        Assert.Single(deleted);
        Assert.StartsWith("blog/", deleted[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeleteComment_requires_resident_policy()
    {
        var method = typeof(BlogController).GetMethod(nameof(BlogController.DeleteComment));
        Assert.NotNull(method);

        var authorize = method!.GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(authorize);
        Assert.Equal("Resident", authorize!.Policy);
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

    [Fact]
    public async Task UpsertManage_png_featured_image_is_converted_to_jpeg()
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

        var mockPosts = new Mock<IBlogPostRepository>();
        mockPosts.Setup(r => r.GetSlugCandidatesAsync()).ReturnsAsync(new List<BlogPost>());
        mockPosts.Setup(r => r.UpsertAsync(It.IsAny<BlogPost>())).ReturnsAsync((BlogPost p) => p);

        var mockUploadHelper = new Mock<IImageUploadHelper>();
        mockUploadHelper
            .Setup(h => h.ConvertAndUploadAsync(It.IsAny<IFormFile>(), ".png", It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((IFormFile _, string _, string prefix, string baseName) =>
                new ImageUploadResult($"{prefix}/{baseName}.jpg", $"{baseName}.jpg", "image/jpeg", 5));

        var mockAudit = new Mock<IAuditLogRepository>();
        mockAudit.Setup(a => a.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var c = CreateController(mockUsers.Object, mockPosts.Object, Mock.Of<IBlogCommentRepository>(),
            Mock.Of<IDocumentFileStore>(), mockAudit.Object, mockUploadHelper.Object);

        var imgBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 }; // PNG magic bytes
        IFormFile formFile = new FormFile(new MemoryStream(imgBytes), 0, imgBytes.Length, "FeaturedImage", "photo.png")
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/png"
        };

        var request = new BlogPostUpsertRequest
        {
            Title = "Test post",
            Content = "Body text",
            FeaturedImage = formFile
        };

        var result = await c.UpsertManage(request);

        var ok = Assert.IsType<OkObjectResult>(result);
        var detail = Assert.IsType<BlogPostDetail>(ok.Value);
        Assert.Equal("image/jpeg", detail.FeaturedImageContentType);
        Assert.EndsWith(".jpg", detail.FeaturedImageDisplayName);
    }

    [Fact]
    public async Task UploadImage_png_is_converted_to_jpeg()
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

        var mockUploadHelper = new Mock<IImageUploadHelper>();
        mockUploadHelper
            .Setup(h => h.ConvertAndUploadAsync(It.IsAny<IFormFile>(), ".png", It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((IFormFile _, string _, string prefix, string baseName) =>
                new ImageUploadResult($"{prefix}/{baseName}.jpg", $"{baseName}.jpg", "image/jpeg", 5));

        var c = CreateController(mockUsers.Object, Mock.Of<IBlogPostRepository>(), Mock.Of<IBlogCommentRepository>(),
            Mock.Of<IDocumentFileStore>(), Mock.Of<IAuditLogRepository>(), mockUploadHelper.Object);

        var imgBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 }; // PNG magic bytes
        IFormFile formFile = new FormFile(new MemoryStream(imgBytes), 0, imgBytes.Length, "file", "photo.png")
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/png"
        };

        var result = await c.UploadImage(formFile);

        var ok = Assert.IsType<OkObjectResult>(result);

        // Verify the returned URL uses .jpg
        var urlProp = ok.Value!.GetType().GetProperty("url");
        Assert.NotNull(urlProp);
        var url = urlProp!.GetValue(ok.Value) as string;
        Assert.NotNull(url);
        Assert.EndsWith(".jpg", url!);
    }

    [Fact]
    public async Task GetPublished_sets_public_cache_control()
    {
        var mockPosts = new Mock<IBlogPostRepository>();
        mockPosts.Setup(r => r.GetPublishedAsync(It.IsAny<DateTime>())).ReturnsAsync(new List<BlogPost>());

        var c = CreateAnonymousController(mockPosts.Object);
        await c.GetPublished();

        Assert.Equal("public, max-age=300", c.Response.Headers["Cache-Control"].ToString());
    }

    [Fact]
    public async Task DownloadFeaturedImage_sets_no_cache_with_etag()
    {
        var mockPosts = new Mock<IBlogPostRepository>();
        mockPosts.Setup(r => r.GetByRouteSegmentAsync("2026-x")).ReturnsAsync(new BlogPost
        {
            Id = Guid.NewGuid(),
            FeaturedImageBlobPath = "blog/guid/photo.jpg",
            PublicSlug = "2026-x",
            Title = "T",
            Content = "c",
            PublishUtc = DateTime.UtcNow,
            AuthorDisplayName = "A"
        });

        var mockFiles = new Mock<IDocumentFileStore>();
        mockFiles.Setup(f => f.DownloadAsync("blog/guid/photo.jpg")).ReturnsAsync(new DocumentFileResult
        {
            Stream = new MemoryStream(new byte[] { 1, 2, 3 }),
            ContentType = "image/jpeg",
            ETag = "\"xyz789\"",
            LastModified = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
        });

        var c = CreateAnonymousController(mockPosts.Object, files: mockFiles.Object);
        var result = await c.DownloadFeaturedImage("2026-x");

        Assert.Equal("public, no-cache", c.Response.Headers["Cache-Control"].ToString());
        var fileResult = Assert.IsType<FileStreamResult>(result);
        Assert.NotNull(fileResult.EntityTag);
        Assert.Equal("\"xyz789\"", fileResult.EntityTag.Tag.ToString());
        Assert.NotNull(fileResult.LastModified);
    }
}
