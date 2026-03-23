using System;
using System.Collections.Generic;
using System.IO;
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
}
