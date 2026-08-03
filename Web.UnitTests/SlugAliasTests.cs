using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Moq;
using Web.Configuration;
using Web.Controllers;
using Web.MockData;
using Web.Models;
using Web.PresentationModels;
using Web.Services;
using Web.Services.Repositories;
using Web.UpdateModels;

namespace Web.UnitTests;

public sealed class SlugAliasTests
{
    // ── EventUrlSlug.EnsureUniquePublicSlug ──

    [Fact]
    public void Event_EnsureUniquePublicSlug_avoids_alias_of_another_event()
    {
        var otherId = Guid.NewGuid();
        var allEvents = new List<CommunityEvent>
        {
            new()
            {
                Id = otherId,
                Title = "Different Title Now",
                PublicSlug = "2025-different-title-now",
                PreviousSlugs = new List<string> { "2025-neighborhood-picnic" },
                StartUtc = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            },
        };

        // A new event wants the slug "2025-neighborhood-picnic" but it's an alias of the other event.
        var newId = Guid.NewGuid();
        var slug = EventUrlSlug.EnsureUniquePublicSlug(
            newId,
            new DateTime(2025, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            "Neighborhood Picnic",
            allEvents
        );

        Assert.NotEqual("2025-neighborhood-picnic", slug);
        Assert.StartsWith("2025-neighborhood-picnic-", slug);
    }

    [Fact]
    public void Event_EnsureUniquePublicSlug_allows_own_alias_reuse()
    {
        // When an event is being saved and the new slug matches its own alias, that's fine.
        var myId = Guid.NewGuid();
        var allEvents = new List<CommunityEvent>
        {
            new()
            {
                Id = myId,
                Title = "Renamed",
                PublicSlug = "2025-renamed",
                PreviousSlugs = new List<string> { "2025-original" },
                StartUtc = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            },
        };

        // Renaming back to the original title should get the original slug (self is skipped in uniqueness).
        var slug = EventUrlSlug.EnsureUniquePublicSlug(
            myId,
            new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            "Original",
            allEvents
        );

        Assert.Equal("2025-original", slug);
    }

    // ── BlogUrlSlug.EnsureUniquePublicSlug ──

    [Fact]
    public void Blog_EnsureUniquePublicSlug_avoids_alias_of_another_post()
    {
        var otherId = Guid.NewGuid();
        var allPosts = new List<BlogPost>
        {
            new()
            {
                Id = otherId,
                Title = "Updated Title",
                PublicSlug = "2026-updated-title",
                PreviousSlugs = new List<string> { "2026-garden-tips" },
                PublishUtc = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            },
        };

        var newId = Guid.NewGuid();
        var slug = BlogUrlSlug.EnsureUniquePublicSlug(
            newId,
            new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            "Garden Tips",
            allPosts
        );

        Assert.NotEqual("2026-garden-tips", slug);
        Assert.StartsWith("2026-garden-tips-", slug);
    }

    // ── MockCommunityEventRepository.GetByRouteSegmentAsync ──

    [Fact]
    public async Task MockEventRepo_GetByRouteSegment_finds_by_previous_slug()
    {
        var repo = new MockCommunityEventRepository();

        // Create an event, then update it so it gets a previous slug.
        var ev = new CommunityEvent
        {
            Id = Guid.NewGuid(),
            Title = "Original Name",
            PublicSlug = "2025-original-name",
            PreviousSlugs = new List<string> { "2025-old-name" },
            StartUtc = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            Signups = new List<EventSignup>(),
        };
        await repo.UpsertAsync(ev);

        // Lookup by current slug works
        var byCurrent = await repo.GetByRouteSegmentAsync("2025-original-name");
        Assert.NotNull(byCurrent);
        Assert.Equal(ev.Id, byCurrent!.Id);

        // Lookup by old slug also works
        var byAlias = await repo.GetByRouteSegmentAsync("2025-old-name");
        Assert.NotNull(byAlias);
        Assert.Equal(ev.Id, byAlias!.Id);
    }

    [Fact]
    public async Task MockEventRepo_GetByRouteSegment_returns_null_for_unknown_slug()
    {
        var repo = new MockCommunityEventRepository();
        var result = await repo.GetByRouteSegmentAsync("2099-does-not-exist");
        Assert.Null(result);
    }

    // ── MockBlogPostRepository.GetByRouteSegmentAsync ──

    [Fact]
    public async Task MockBlogRepo_GetByRouteSegment_finds_by_previous_slug()
    {
        var repo = new MockBlogPostRepository();

        var post = new BlogPost
        {
            Id = Guid.NewGuid(),
            Title = "New Title",
            PublicSlug = "2026-new-title",
            PreviousSlugs = new List<string> { "2026-old-title" },
            PublishUtc = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            Content = "body",
            AuthorDisplayName = "Test",
        };
        await repo.UpsertAsync(post);

        var byCurrent = await repo.GetByRouteSegmentAsync("2026-new-title");
        Assert.NotNull(byCurrent);
        Assert.Equal(post.Id, byCurrent!.Id);

        var byAlias = await repo.GetByRouteSegmentAsync("2026-old-title");
        Assert.NotNull(byAlias);
        Assert.Equal(post.Id, byAlias!.Id);
    }

    // ── EventsController.UpsertManage – alias accumulation ──

    private const string IdentityProviderClaim = "http://schemas.microsoft.com/identity/claims/identityprovider";

    private static string UniqueId(string nameId, string idp = "google.com") => $"{idp}{nameId}";

    private static EventsController CreateEventsController(
        IUserRepository users,
        ICommunityEventRepository events,
        string nameId = "u1"
    )
    {
        var c = new EventsController(
            new CurrentUserAccessor(users),
            events,
            Mock.Of<IHomeRepository>(),
            Mock.Of<IDocumentFileStore>(),
            Mock.Of<IAuditLogRepository>(),
            Mock.Of<IOgThumbnailService>(),
            DefaultImageUploadHelper(),
            Options.Create(new DocumentStorageOptions { MaxUploadBytes = 1024 * 1024 }),
            Options.Create(new JsonOptions())
        );

        c.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(
                    new ClaimsIdentity(
                        new[]
                        {
                            new Claim(ClaimTypes.NameIdentifier, nameId),
                            new Claim(IdentityProviderClaim, "google.com"),
                        },
                        "Test"
                    )
                ),
            },
        };
        return c;
    }

    private static BlogController CreateBlogController(
        IUserRepository users,
        IBlogPostRepository posts,
        string nameId = "u1"
    )
    {
        var c = new BlogController(
            new CurrentUserAccessor(users),
            posts,
            Mock.Of<IBlogCommentRepository>(),
            Mock.Of<IDocumentFileStore>(),
            Mock.Of<IAuditLogRepository>(),
            DefaultImageUploadHelper(),
            Options.Create(new DocumentStorageOptions { MaxUploadBytes = 1024 * 1024 })
        );

        c.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(
                    new ClaimsIdentity(
                        new[]
                        {
                            new Claim(ClaimTypes.NameIdentifier, nameId),
                            new Claim(IdentityProviderClaim, "google.com"),
                        },
                        "Test"
                    )
                ),
            },
        };
        return c;
    }

    private static IImageUploadHelper DefaultImageUploadHelper()
    {
        var mock = new Mock<IImageUploadHelper>();
        mock.Setup(h =>
                h.ConvertAndUploadAsync(
                    It.IsAny<IFormFile>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string>()
                )
            )
            .ReturnsAsync(
                (IFormFile f, string ext, string prefix, string baseName) =>
                    new ImageUploadResult(
                        $"{prefix}/{baseName}{ext.ToLowerInvariant()}",
                        $"{baseName}{ext.ToLowerInvariant()}",
                        ImageContentTypes.FromExtension(ext),
                        f.Length
                    )
            );
        return mock.Object;
    }

    private static User AdminUser(string uniqueId) =>
        new()
        {
            UniqueId = uniqueId,
            GivenName = "Test",
            Surname = "Admin",
            Roles = new List<User.Role> { User.Role.Resident, User.Role.Administrator },
        };

    [Fact]
    public async Task EventUpsert_preserves_old_slug_when_title_changes()
    {
        var uniqueId = UniqueId("u1");
        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(AdminUser(uniqueId));

        var eventId = Guid.NewGuid();
        var existing = new CommunityEvent
        {
            Id = eventId,
            Title = "Original Title",
            PublicSlug = "2025-original-title",
            StartUtc = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            Signups = new List<EventSignup>(),
            CreatedByUniqueId = uniqueId,
            CreatedUtc = DateTime.UtcNow.AddDays(-1),
        };

        CommunityEvent saved = null;
        var mockEvents = new Mock<ICommunityEventRepository>();
        mockEvents
            .Setup(r => r.ReadAsync(eventId))
            .ReturnsAsync(new CommunityEventReadResult { Event = existing, ETag = "\"e1\"" });
        mockEvents.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<CommunityEvent> { existing });
        mockEvents
            .Setup(r => r.ReplaceAsync(It.IsAny<CommunityEvent>(), It.IsAny<string>()))
            .Callback<CommunityEvent, string>((e, _) => saved = e)
            .ReturnsAsync((CommunityEvent e, string _) => e);

        var mockAudit = new Mock<IAuditLogRepository>();
        mockAudit.Setup(a => a.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var c = CreateEventsController(mockUsers.Object, mockEvents.Object);
        var result = await c.UpsertManage(
            new EventUpsertRequest
            {
                Id = eventId,
                Title = "New Title",
                StartUtc = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            }
        );

        Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(saved);
        Assert.Equal("2025-new-title", saved!.PublicSlug);
        Assert.Contains("2025-original-title", saved.PreviousSlugs);
    }

    [Fact]
    public async Task EventUpsert_does_not_add_alias_when_slug_unchanged()
    {
        var uniqueId = UniqueId("u1");
        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(AdminUser(uniqueId));

        var eventId = Guid.NewGuid();
        var existing = new CommunityEvent
        {
            Id = eventId,
            Title = "Same Title",
            PublicSlug = "2025-same-title",
            StartUtc = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            Signups = new List<EventSignup>(),
            CreatedByUniqueId = uniqueId,
            CreatedUtc = DateTime.UtcNow.AddDays(-1),
        };

        CommunityEvent saved = null;
        var mockEvents = new Mock<ICommunityEventRepository>();
        mockEvents
            .Setup(r => r.ReadAsync(eventId))
            .ReturnsAsync(new CommunityEventReadResult { Event = existing, ETag = "\"e1\"" });
        mockEvents.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<CommunityEvent> { existing });
        mockEvents
            .Setup(r => r.ReplaceAsync(It.IsAny<CommunityEvent>(), It.IsAny<string>()))
            .Callback<CommunityEvent, string>((e, _) => saved = e)
            .ReturnsAsync((CommunityEvent e, string _) => e);

        var mockAudit = new Mock<IAuditLogRepository>();
        mockAudit.Setup(a => a.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var c = CreateEventsController(mockUsers.Object, mockEvents.Object);
        var result = await c.UpsertManage(
            new EventUpsertRequest
            {
                Id = eventId,
                Title = "Same Title",
                Description = "Updated description only",
                StartUtc = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            }
        );

        Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(saved);
        Assert.Equal("2025-same-title", saved!.PublicSlug);
        Assert.Empty(saved.PreviousSlugs);
    }

    [Fact]
    public async Task EventUpsert_does_not_duplicate_alias_on_repeated_rename()
    {
        var uniqueId = UniqueId("u1");
        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(AdminUser(uniqueId));

        var eventId = Guid.NewGuid();
        var existing = new CommunityEvent
        {
            Id = eventId,
            Title = "Second Title",
            PublicSlug = "2025-second-title",
            PreviousSlugs = new List<string> { "2025-first-title" },
            StartUtc = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            Signups = new List<EventSignup>(),
            CreatedByUniqueId = uniqueId,
            CreatedUtc = DateTime.UtcNow.AddDays(-1),
        };

        CommunityEvent saved = null;
        var mockEvents = new Mock<ICommunityEventRepository>();
        mockEvents
            .Setup(r => r.ReadAsync(eventId))
            .ReturnsAsync(new CommunityEventReadResult { Event = existing, ETag = "\"e1\"" });
        mockEvents.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<CommunityEvent> { existing });
        mockEvents
            .Setup(r => r.ReplaceAsync(It.IsAny<CommunityEvent>(), It.IsAny<string>()))
            .Callback<CommunityEvent, string>((e, _) => saved = e)
            .ReturnsAsync((CommunityEvent e, string _) => e);

        var mockAudit = new Mock<IAuditLogRepository>();
        mockAudit.Setup(a => a.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var c = CreateEventsController(mockUsers.Object, mockEvents.Object);
        var result = await c.UpsertManage(
            new EventUpsertRequest
            {
                Id = eventId,
                Title = "Third Title",
                StartUtc = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            }
        );

        Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(saved);
        Assert.Equal("2025-third-title", saved!.PublicSlug);
        Assert.Equal(2, saved.PreviousSlugs.Count);
        Assert.Contains("2025-first-title", saved.PreviousSlugs);
        Assert.Contains("2025-second-title", saved.PreviousSlugs);
    }

    [Fact]
    public async Task EventUpsert_create_does_not_populate_aliases()
    {
        var uniqueId = UniqueId("u1");
        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(AdminUser(uniqueId));

        CommunityEvent saved = null;
        var mockEvents = new Mock<ICommunityEventRepository>();
        mockEvents.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<CommunityEvent>());
        mockEvents
            .Setup(r => r.UpsertAsync(It.IsAny<CommunityEvent>()))
            .Callback<CommunityEvent>(e => saved = e)
            .ReturnsAsync((CommunityEvent e) => e);

        var mockAudit = new Mock<IAuditLogRepository>();
        mockAudit.Setup(a => a.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var c = CreateEventsController(mockUsers.Object, mockEvents.Object);
        var result = await c.UpsertManage(
            new EventUpsertRequest
            {
                Title = "Brand New",
                StartUtc = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            }
        );

        Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(saved);
        Assert.Empty(saved!.PreviousSlugs);
    }

    // ── BlogController.UpsertManage – alias accumulation ──

    [Fact]
    public async Task BlogUpsert_preserves_old_slug_when_title_changes()
    {
        var uniqueId = UniqueId("u1");
        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(AdminUser(uniqueId));

        var postId = Guid.NewGuid();
        var existing = new BlogPost
        {
            Id = postId,
            Title = "Original Title",
            PublicSlug = "2026-original-title",
            Content = "body",
            PublishUtc = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            AuthorDisplayName = "Test Admin",
            CreatedByUniqueId = uniqueId,
            CreatedUtc = DateTime.UtcNow.AddDays(-1),
        };

        BlogPost saved = null;
        var mockPosts = new Mock<IBlogPostRepository>();
        mockPosts
            .Setup(r => r.ReadAsync(postId))
            .ReturnsAsync(new BlogPostReadResult { Post = existing, ETag = "\"e1\"" });
        mockPosts.Setup(r => r.GetSlugCandidatesAsync()).ReturnsAsync(new List<BlogPost> { existing });
        mockPosts
            .Setup(r => r.ReplaceAsync(It.IsAny<BlogPost>(), It.IsAny<string>()))
            .Callback<BlogPost, string>((p, _) => saved = p)
            .ReturnsAsync((BlogPost p, string _) => p);

        var mockAudit = new Mock<IAuditLogRepository>();
        mockAudit.Setup(a => a.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var c = CreateBlogController(mockUsers.Object, mockPosts.Object);
        var result = await c.UpsertManage(
            new BlogPostUpsertRequest
            {
                Id = postId,
                Title = "New Title",
                Content = "body",
                PublishUtc = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            }
        );

        Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(saved);
        Assert.Equal("2026-new-title", saved!.PublicSlug);
        Assert.Contains("2026-original-title", saved.PreviousSlugs);
    }

    [Fact]
    public async Task BlogUpsert_does_not_add_alias_when_slug_unchanged()
    {
        var uniqueId = UniqueId("u1");
        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(AdminUser(uniqueId));

        var postId = Guid.NewGuid();
        var existing = new BlogPost
        {
            Id = postId,
            Title = "Same Title",
            PublicSlug = "2026-same-title",
            Content = "body",
            PublishUtc = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            AuthorDisplayName = "Test Admin",
            CreatedByUniqueId = uniqueId,
            CreatedUtc = DateTime.UtcNow.AddDays(-1),
        };

        BlogPost saved = null;
        var mockPosts = new Mock<IBlogPostRepository>();
        mockPosts
            .Setup(r => r.ReadAsync(postId))
            .ReturnsAsync(new BlogPostReadResult { Post = existing, ETag = "\"e1\"" });
        mockPosts.Setup(r => r.GetSlugCandidatesAsync()).ReturnsAsync(new List<BlogPost> { existing });
        mockPosts
            .Setup(r => r.ReplaceAsync(It.IsAny<BlogPost>(), It.IsAny<string>()))
            .Callback<BlogPost, string>((p, _) => saved = p)
            .ReturnsAsync((BlogPost p, string _) => p);

        var mockAudit = new Mock<IAuditLogRepository>();
        mockAudit.Setup(a => a.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var c = CreateBlogController(mockUsers.Object, mockPosts.Object);
        var result = await c.UpsertManage(
            new BlogPostUpsertRequest
            {
                Id = postId,
                Title = "Same Title",
                Content = "updated body",
                PublishUtc = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            }
        );

        Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(saved);
        Assert.Equal("2026-same-title", saved!.PublicSlug);
        Assert.Empty(saved.PreviousSlugs);
    }
}
