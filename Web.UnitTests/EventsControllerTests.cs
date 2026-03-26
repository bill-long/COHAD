using System;
using System.Collections.Generic;
using System.IO;
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

public sealed class EventsControllerTests
{
    private const string IdentityProviderClaim = "http://schemas.microsoft.com/identity/claims/identityprovider";

    private static EventsController CreateController(
        IUserRepository users,
        ICommunityEventRepository events,
        IDocumentFileStore fileStore,
        IAuditLogRepository auditLog,
        string nameId = "u1",
        string idp = "google.com")
    {
        var c = new EventsController(
            users,
            events,
            fileStore,
            auditLog,
            new SkiaSharpOgThumbnailService(),
            Options.Create(new DocumentStorageOptions { MaxUploadBytes = 1024 * 1024 }));

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

        var c = CreateController(mockUsers.Object, Mock.Of<ICommunityEventRepository>(), Mock.Of<IDocumentFileStore>(), Mock.Of<IAuditLogRepository>());
        var result = await c.GetManage();

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task GetManage_returns_events_for_resident_plus_other_role()
    {
        var uniqueId = UniqueId("u1");
        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(new User
        {
            UniqueId = uniqueId,
            Roles = new List<User.Role> { User.Role.Resident, User.Role.Administrator }
        });

        var mockEvents = new Mock<ICommunityEventRepository>();
        mockEvents.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<CommunityEvent>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Title = "Neighborhood Mixer",
                StartUtc = DateTime.UtcNow.AddDays(1),
                AllowSignups = true
            }
        });

        var c = CreateController(mockUsers.Object, mockEvents.Object, Mock.Of<IDocumentFileStore>(), Mock.Of<IAuditLogRepository>());
        var result = await c.GetManage();

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<ManageEventsPayload>(ok.Value);
        Assert.Single(payload.Upcoming);
        Assert.Empty(payload.Past);
    }

    [Fact]
    public async Task GetManage_splits_upcoming_and_past_by_grace_window()
    {
        var uniqueId = UniqueId("u1");
        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(new User
        {
            UniqueId = uniqueId,
            Roles = new List<User.Role> { User.Role.Resident, User.Role.Administrator }
        });

        var now = DateTime.UtcNow;
        var mockEvents = new Mock<ICommunityEventRepository>();
        mockEvents.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<CommunityEvent>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Title = "Future",
                StartUtc = now.AddDays(1),
                AllowSignups = true
            },
            new()
            {
                Id = Guid.NewGuid(),
                Title = "Old",
                StartUtc = now.AddHours(-10),
                AllowSignups = false
            }
        });

        var c = CreateController(mockUsers.Object, mockEvents.Object, Mock.Of<IDocumentFileStore>(), Mock.Of<IAuditLogRepository>());
        var result = await c.GetManage();

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<ManageEventsPayload>(ok.Value);
        Assert.Single(payload.Upcoming);
        Assert.Equal("Future", payload.Upcoming[0].Title);
        Assert.Single(payload.Past);
        Assert.Equal("Old", payload.Past[0].Title);
    }

    [Fact]
    public async Task SignUp_returns_BadRequest_when_signups_are_disabled()
    {
        var uniqueId = UniqueId("u1");
        var eventId = Guid.NewGuid();
        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(new User
        {
            UniqueId = uniqueId,
            GivenName = "Mock",
            Surname = "Resident",
            Roles = new List<User.Role> { User.Role.Resident }
        });

        var mockEvents = new Mock<ICommunityEventRepository>();
        mockEvents.Setup(r => r.GetByRouteSegmentAsync(eventId.ToString("D"))).ReturnsAsync(new CommunityEvent
        {
            Id = eventId,
            Title = "Closed Event",
            StartUtc = DateTime.UtcNow.AddDays(1),
            AllowSignups = false
        });

        var c = CreateController(mockUsers.Object, mockEvents.Object, Mock.Of<IDocumentFileStore>(), Mock.Of<IAuditLogRepository>());
        var result = await c.SignUp(eventId.ToString("D"), new EventSignupRequest { Adults = 1, Children = 0 });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task SignUp_returns_BadRequest_when_attendee_counts_exceed_max()
    {
        var uniqueId = UniqueId("u1");
        var eventId = Guid.NewGuid();
        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(new User
        {
            UniqueId = uniqueId,
            GivenName = "Mock",
            Surname = "Resident",
            Roles = new List<User.Role> { User.Role.Resident }
        });

        var mockEvents = new Mock<ICommunityEventRepository>();
        mockEvents.Setup(r => r.GetByRouteSegmentAsync(eventId.ToString("D"))).ReturnsAsync(new CommunityEvent
        {
            Id = eventId,
            Title = "Open Event",
            StartUtc = DateTime.UtcNow.AddDays(2),
            AllowSignups = true,
            Signups = new List<EventSignup>()
        });

        var c = CreateController(mockUsers.Object, mockEvents.Object, Mock.Of<IDocumentFileStore>(), Mock.Of<IAuditLogRepository>());
        var result = await c.SignUp(eventId.ToString("D"), new EventSignupRequest { Adults = 51, Children = 0 });

        Assert.IsType<BadRequestObjectResult>(result);
        mockEvents.Verify(r => r.ReadAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task SignUp_adds_signup_and_replaces_event_with_etag()
    {
        var uniqueId = UniqueId("u1");
        var eventId = Guid.NewGuid();
        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(new User
        {
            UniqueId = uniqueId,
            GivenName = "Mock",
            Surname = "Resident",
            Emails = "mock@cohad.local",
            Roles = new List<User.Role> { User.Role.Resident }
        });

        var stored = new CommunityEvent
        {
            Id = eventId,
            Title = "Open Event",
            StartUtc = DateTime.UtcNow.AddDays(2),
            AllowSignups = true,
            Signups = new List<EventSignup>()
        };

        CommunityEvent replaced = null;
        var mockEvents = new Mock<ICommunityEventRepository>();
        mockEvents.Setup(r => r.GetByRouteSegmentAsync(eventId.ToString("D"))).ReturnsAsync(stored);
        mockEvents.Setup(r => r.ReadAsync(eventId)).ReturnsAsync(new CommunityEventReadResult
        {
            Event = stored,
            ETag = "\"e1\""
        });
        mockEvents.Setup(r => r.ReplaceAsync(It.IsAny<CommunityEvent>(), It.IsAny<string>()))
            .Callback<CommunityEvent, string>((e, _) => replaced = e)
            .ReturnsAsync((CommunityEvent e, string _) => e);

        var mockAudit = new Mock<IAuditLogRepository>();
        mockAudit.Setup(a => a.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var c = CreateController(mockUsers.Object, mockEvents.Object, Mock.Of<IDocumentFileStore>(), mockAudit.Object);
        var result = await c.SignUp(eventId.ToString("D"), new EventSignupRequest
        {
            Adults = 2,
            Children = 1,
            AdultNames = new List<string> { "Alex", "Jordan" },
            ChildNames = new List<string> { "Sam" }
        });

        Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(replaced);
        Assert.Single(replaced!.Signups);
        Assert.Equal(2, replaced.Signups[0].Adults);
        Assert.Equal(1, replaced.Signups[0].Children);

        mockAudit.Verify(a => a.AddAsync(It.Is<NewAuditLogEntry>(e =>
            e.SubjectId == eventId.ToString("D") &&
            e.SubjectName == "Open Event" &&
            e.UserId == uniqueId &&
            e.Action == "Signed up for event. (2 adults, 1 children)")), Times.Once);
    }

    [Fact]
    public async Task SignUp_updates_existing_signup_and_writes_audit()
    {
        var uniqueId = UniqueId("u1");
        var eventId = Guid.NewGuid();
        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(new User
        {
            UniqueId = uniqueId,
            GivenName = "Mock",
            Surname = "Resident",
            Emails = "mock@cohad.local",
            Roles = new List<User.Role> { User.Role.Resident }
        });

        var stored = new CommunityEvent
        {
            Id = eventId,
            Title = "Open Event",
            StartUtc = DateTime.UtcNow.AddDays(2),
            AllowSignups = true,
            Signups = new List<EventSignup>
            {
                new EventSignup
                {
                    UserUniqueId = uniqueId,
                    Adults = 1,
                    Children = 0
                }
            }
        };

        var mockEvents = new Mock<ICommunityEventRepository>();
        mockEvents.Setup(r => r.GetByRouteSegmentAsync(eventId.ToString("D"))).ReturnsAsync(stored);
        mockEvents.Setup(r => r.ReadAsync(eventId)).ReturnsAsync(new CommunityEventReadResult
        {
            Event = stored,
            ETag = "\"e1\""
        });
        mockEvents.Setup(r => r.ReplaceAsync(It.IsAny<CommunityEvent>(), It.IsAny<string>()))
            .ReturnsAsync((CommunityEvent e, string _) => e);

        var mockAudit = new Mock<IAuditLogRepository>();
        mockAudit.Setup(a => a.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var c = CreateController(mockUsers.Object, mockEvents.Object, Mock.Of<IDocumentFileStore>(), mockAudit.Object);
        var result = await c.SignUp(eventId.ToString("D"), new EventSignupRequest
        {
            Adults = 2,
            Children = 1
        });

        Assert.IsType<OkObjectResult>(result);

        mockAudit.Verify(a => a.AddAsync(It.Is<NewAuditLogEntry>(e =>
            e.SubjectId == eventId.ToString("D") &&
            e.Action == "Updated event signup. (2 adults, 1 children)")), Times.Once);
    }

    [Fact]
    public async Task SignUp_returns_NotFound_when_event_deleted_before_replace()
    {
        var uniqueId = UniqueId("u1");
        var eventId = Guid.NewGuid();
        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(new User
        {
            UniqueId = uniqueId,
            GivenName = "Mock",
            Surname = "Resident",
            Emails = "mock@cohad.local",
            Roles = new List<User.Role> { User.Role.Resident }
        });

        var stored = new CommunityEvent
        {
            Id = eventId,
            Title = "Open Event",
            StartUtc = DateTime.UtcNow.AddDays(2),
            AllowSignups = true,
            Signups = new List<EventSignup>()
        };

        var mockEvents = new Mock<ICommunityEventRepository>();
        mockEvents.Setup(r => r.GetByRouteSegmentAsync(eventId.ToString("D"))).ReturnsAsync(stored);
        mockEvents.Setup(r => r.ReadAsync(eventId)).ReturnsAsync(new CommunityEventReadResult
        {
            Event = stored,
            ETag = "\"e1\""
        });
        mockEvents.Setup(r => r.ReplaceAsync(It.IsAny<CommunityEvent>(), It.IsAny<string>()))
            .ThrowsAsync(new Microsoft.Azure.Cosmos.CosmosException("Not found", HttpStatusCode.NotFound, 0, string.Empty, 0));

        var c = CreateController(mockUsers.Object, mockEvents.Object, Mock.Of<IDocumentFileStore>(), Mock.Of<IAuditLogRepository>());
        var result = await c.SignUp(eventId.ToString("D"), new EventSignupRequest { Adults = 1, Children = 0 });

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task DownloadPromoMedia_returns_FileStreamResult_without_FileDownloadName_for_inline_embed()
    {
        var eventId = Guid.NewGuid();
        var mockEvents = new Mock<ICommunityEventRepository>();
        mockEvents.Setup(r => r.GetByRouteSegmentAsync(eventId.ToString("D"))).ReturnsAsync(new CommunityEvent
        {
            Id = eventId,
            Title = "Mixer",
            StartUtc = DateTime.UtcNow.AddDays(1),
            PromoMediaBlobPath = "events/promo.jpg",
            PromoMediaDisplayName = "mixer-flyer.jpg",
            PromoMediaContentType = "image/jpeg"
        });

        var mockFileStore = new Mock<IDocumentFileStore>();
        mockFileStore.Setup(s => s.DownloadAsync("events/promo.jpg")).ReturnsAsync(new DocumentFileResult
        {
            Stream = new MemoryStream([1, 2, 3]),
            ContentType = "image/jpeg"
        });

        var c = CreateController(Mock.Of<IUserRepository>(), mockEvents.Object, mockFileStore.Object, Mock.Of<IAuditLogRepository>());
        var result = await c.DownloadPromoMedia(eventId.ToString("D"));

        var fileResult = Assert.IsType<FileStreamResult>(result);
        Assert.True(string.IsNullOrEmpty(fileResult.FileDownloadName));
        Assert.Equal("image/jpeg", fileResult.ContentType);
    }

    [Fact]
    public async Task DownloadPromoMedia_returns_NotFound_when_event_has_no_promo()
    {
        var eventId = Guid.NewGuid();
        var mockEvents = new Mock<ICommunityEventRepository>();
        mockEvents.Setup(r => r.GetByRouteSegmentAsync(eventId.ToString("D"))).ReturnsAsync(new CommunityEvent
        {
            Id = eventId,
            Title = "Mixer",
            StartUtc = DateTime.UtcNow.AddDays(1),
            PromoMediaBlobPath = null
        });

        var c = CreateController(Mock.Of<IUserRepository>(), mockEvents.Object, Mock.Of<IDocumentFileStore>(), Mock.Of<IAuditLogRepository>());
        var result = await c.DownloadPromoMedia(eventId.ToString("D"));

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task DownloadPromoThumb_returns_NotFound_when_event_has_no_promo()
    {
        var eventId = Guid.NewGuid();
        var mockEvents = new Mock<ICommunityEventRepository>();
        mockEvents.Setup(r => r.GetByRouteSegmentAsync(eventId.ToString("D"))).ReturnsAsync(new CommunityEvent
        {
            Id = eventId,
            Title = "Mixer",
            StartUtc = DateTime.UtcNow.AddDays(1),
            PromoMediaBlobPath = null
        });

        var c = CreateController(Mock.Of<IUserRepository>(), mockEvents.Object, Mock.Of<IDocumentFileStore>(), Mock.Of<IAuditLogRepository>());
        var result = await c.DownloadPromoThumb(eventId.ToString("D"));

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task DownloadPromoThumb_returns_existing_thumbnail_when_thumb_blob_path_is_set()
    {
        var eventId = Guid.NewGuid();
        var thumbPath = $"events/{eventId:D}/og-thumb.jpg";
        var mockEvents = new Mock<ICommunityEventRepository>();
        mockEvents.Setup(r => r.GetByRouteSegmentAsync("test-event")).ReturnsAsync(new CommunityEvent
        {
            Id = eventId,
            Title = "Test",
            StartUtc = DateTime.UtcNow.AddDays(1),
            PromoMediaBlobPath = "events/promo.png",
            PromoMediaContentType = "image/png",
            PromoMediaThumbBlobPath = thumbPath
        });

        var mockFileStore = new Mock<IDocumentFileStore>();
        mockFileStore.Setup(s => s.DownloadAsync(thumbPath)).ReturnsAsync(new DocumentFileResult
        {
            Stream = new MemoryStream([1, 2, 3]),
            ContentType = "image/jpeg"
        });

        var c = CreateController(Mock.Of<IUserRepository>(), mockEvents.Object, mockFileStore.Object, Mock.Of<IAuditLogRepository>());
        var result = await c.DownloadPromoThumb("test-event");

        var fileResult = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("image/jpeg", fileResult.ContentType);
    }

    [Fact]
    public async Task DownloadPromoThumb_lazy_generates_when_thumb_blob_path_is_null()
    {
        var eventId = Guid.NewGuid();
        var thumbPath = $"events/{eventId:D}/og-thumb.jpg";
        var mockEvents = new Mock<ICommunityEventRepository>();
        mockEvents.Setup(r => r.GetByRouteSegmentAsync("test-event")).ReturnsAsync(new CommunityEvent
        {
            Id = eventId,
            Title = "Test",
            StartUtc = DateTime.UtcNow.AddDays(1),
            PromoMediaBlobPath = "events/promo.png",
            PromoMediaContentType = "image/png",
            PromoMediaThumbBlobPath = null
        });

        // Conventional thumb path not found yet
        var mockFileStore = new Mock<IDocumentFileStore>();
        mockFileStore.Setup(s => s.DownloadAsync(thumbPath)).ReturnsAsync((DocumentFileResult)null);

        // Return a small valid PNG for the original (1x1 white pixel)
        var pngBytes = CreateMinimalPng();
        mockFileStore.Setup(s => s.DownloadAsync("events/promo.png")).ReturnsAsync(new DocumentFileResult
        {
            Stream = new MemoryStream(pngBytes),
            ContentType = "image/png"
        });

        mockEvents.Setup(r => r.ReadAsync(eventId)).ReturnsAsync(new CommunityEventReadResult
        {
            Event = new CommunityEvent { Id = eventId, Title = "Test" },
            ETag = "\"e1\""
        });
        mockEvents.Setup(r => r.ReplaceAsync(It.IsAny<CommunityEvent>(), It.IsAny<string>()))
            .ReturnsAsync((CommunityEvent e, string _) => e);

        var c = CreateController(Mock.Of<IUserRepository>(), mockEvents.Object, mockFileStore.Object, Mock.Of<IAuditLogRepository>());
        var result = await c.DownloadPromoThumb("test-event");

        var fileResult = Assert.IsType<FileContentResult>(result);
        Assert.Equal("image/jpeg", fileResult.ContentType);
        mockFileStore.Verify(s => s.UploadAsync(thumbPath, It.IsAny<Stream>(), "image/jpeg"), Times.Once);
    }

    private static byte[] CreateMinimalPng()
    {
        using var bmp = new SkiaSharp.SKBitmap(100, 100);
        bmp.Erase(SkiaSharp.SKColors.White);
        using var image = SkiaSharp.SKImage.FromBitmap(bmp);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public async Task GetUpcoming_passes_minStartUtc_six_hours_before_now_to_repository()
    {
        DateTime? capturedMin = null;
        var mockEvents = new Mock<ICommunityEventRepository>();
        mockEvents
            .Setup(r => r.GetWithStartUtcOnOrAfterAsync(It.IsAny<DateTime>()))
            .Callback<DateTime>(d => capturedMin = d)
            .ReturnsAsync(new List<CommunityEvent>());

        var c = CreateController(Mock.Of<IUserRepository>(), mockEvents.Object, Mock.Of<IDocumentFileStore>(), Mock.Of<IAuditLogRepository>());
        var baselineUtc = DateTime.UtcNow;
        await c.GetUpcoming();

        Assert.NotNull(capturedMin);
        var expected = baselineUtc - TimeSpan.FromHours(6);
        var delta = (capturedMin.Value - expected).Duration();
        Assert.True(delta < TimeSpan.FromSeconds(3), $"Expected minStartUtc near {expected:o}, got {capturedMin.Value:o}");
    }

    [Fact]
    public async Task GetUpcoming_returns_events_ordered_by_StartUtc()
    {
        var earlier = Guid.NewGuid();
        var later = Guid.NewGuid();
        var mockEvents = new Mock<ICommunityEventRepository>();
        mockEvents.Setup(r => r.GetWithStartUtcOnOrAfterAsync(It.IsAny<DateTime>())).ReturnsAsync(new List<CommunityEvent>
        {
            new()
            {
                Id = later,
                Title = "Later",
                StartUtc = DateTime.UtcNow.AddDays(2)
            },
            new()
            {
                Id = earlier,
                Title = "Earlier",
                StartUtc = DateTime.UtcNow.AddDays(1)
            }
        });

        var c = CreateController(Mock.Of<IUserRepository>(), mockEvents.Object, Mock.Of<IDocumentFileStore>(), Mock.Of<IAuditLogRepository>());
        var result = await c.GetUpcoming();

        var ok = Assert.IsType<OkObjectResult>(result);
        var list = Assert.IsAssignableFrom<IReadOnlyList<CommunityEventCard>>(ok.Value);
        Assert.Equal(2, list.Count);
        Assert.Equal("Earlier", list[0].Title);
        Assert.Equal("Later", list[1].Title);
    }

    [Fact]
    public async Task GetNextUpcoming_returns_NotFound_when_no_events_in_window()
    {
        var mockEvents = new Mock<ICommunityEventRepository>();
        mockEvents.Setup(r => r.GetWithStartUtcOnOrAfterAsync(It.IsAny<DateTime>())).ReturnsAsync(new List<CommunityEvent>());

        var c = CreateController(Mock.Of<IUserRepository>(), mockEvents.Object, Mock.Of<IDocumentFileStore>(), Mock.Of<IAuditLogRepository>());
        var result = await c.GetNextUpcoming();

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetNextUpcoming_returns_earliest_event_in_window()
    {
        var earliestId = Guid.NewGuid();
        var mockEvents = new Mock<ICommunityEventRepository>();
        mockEvents.Setup(r => r.GetWithStartUtcOnOrAfterAsync(It.IsAny<DateTime>())).ReturnsAsync(new List<CommunityEvent>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Title = "Second",
                StartUtc = DateTime.UtcNow.AddDays(3)
            },
            new()
            {
                Id = earliestId,
                Title = "First",
                StartUtc = DateTime.UtcNow.AddDays(1)
            }
        });

        var c = CreateController(Mock.Of<IUserRepository>(), mockEvents.Object, Mock.Of<IDocumentFileStore>(), Mock.Of<IAuditLogRepository>());
        var result = await c.GetNextUpcoming();

        var ok = Assert.IsType<OkObjectResult>(result);
        var card = Assert.IsType<CommunityEventCard>(ok.Value);
        Assert.Equal(earliestId, card.Id);
        Assert.Equal("First", card.Title);
    }
}
