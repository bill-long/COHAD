using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

public sealed class CommitteeControllerTests
{
    private const string IdentityProviderClaim = "http://schemas.microsoft.com/identity/claims/identityprovider";
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly Guid AliceResidentId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid BobResidentId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid NewPersonResidentId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid SampleHomeId = Guid.Parse("99999999-9999-9999-9999-999999999999");

    private static Resident AliceResident() =>
        new Resident
        {
            Id = AliceResidentId,
            HomeId = SampleHomeId,
            GivenName = "Alice",
            Surname = "",
            EmailAddresses = new List<EmailAddress> { new EmailAddress { Address = "alice@example.com" } },
        };

    private static Resident BobResident() =>
        new Resident
        {
            Id = BobResidentId,
            HomeId = SampleHomeId,
            GivenName = "Bob",
            Surname = "",
            EmailAddresses = new List<EmailAddress> { new EmailAddress { Address = "bob@example.com" } },
        };

    private static Resident NewPersonResident() =>
        new Resident
        {
            Id = NewPersonResidentId,
            HomeId = SampleHomeId,
            GivenName = "New",
            Surname = "Person",
            EmailAddresses = new List<EmailAddress> { new EmailAddress { Address = "new@example.com" } },
        };

    private static Mock<IResidentRepository> DefaultResidentRepoMock()
    {
        var allResidents = new Dictionary<Guid, Resident>
        {
            { AliceResidentId, AliceResident() },
            { BobResidentId, BobResident() },
            { NewPersonResidentId, NewPersonResident() },
        };

        var mock = new Mock<IResidentRepository>();
        mock.Setup(r => r.GetByIdsAsync(It.IsAny<IEnumerable<Guid>>()))
            .ReturnsAsync(
                (IEnumerable<Guid> ids) =>
                    ids.Where(id => allResidents.ContainsKey(id)).Select(id => allResidents[id]).ToList()
            );
        mock.Setup(r => r.GetByIdAsync(It.IsAny<Guid>())).ReturnsAsync((Guid id) => allResidents.GetValueOrDefault(id));
        return mock;
    }

    private static CommitteeController CreateController(
        ICommitteeRepository? committeeRepo = null,
        IResidentRepository? residentRepo = null,
        IDocumentFileStore? fileStore = null,
        IImageUploadHelper? imageUploadHelper = null,
        IUserRepository? userRepo = null,
        IAuditLogRepository? auditLogRepo = null,
        CommitteeListCache? cache = null,
        string nameId = "u1",
        string idp = "google.com",
        IHeldMessageRepository? heldMessageRepo = null,
        IEmailJobRepository? emailJobRepo = null,
        IServiceProvider? serviceProvider = null,
        ILogger<CommitteeController>? logger = null,
        INotificationService? notificationService = null
    )
    {
        committeeRepo ??= Mock.Of<ICommitteeRepository>();
        notificationService ??= new NotificationService(
            new MockNotificationRepository(),
            new NoOpNotificationRealtimeNotifier(),
            NullLogger<NotificationService>.Instance
        );
        residentRepo ??= DefaultResidentRepoMock().Object;
        fileStore ??= Mock.Of<IDocumentFileStore>();
        imageUploadHelper ??= DefaultImageUploadHelper();
        userRepo ??= AdminUserRepo(nameId, idp);
        auditLogRepo ??= Mock.Of<IAuditLogRepository>();
        cache ??= new CommitteeListCache(committeeRepo, new MemoryCache(new MemoryCacheOptions()), WebJsonOptions);
        heldMessageRepo ??= Mock.Of<IHeldMessageRepository>();
        emailJobRepo ??= Mock.Of<IEmailJobRepository>();

        var c = new CommitteeController(
            committeeRepo,
            residentRepo,
            new CurrentUserAccessor(userRepo),
            auditLogRepo,
            cache,
            fileStore,
            imageUploadHelper,
            heldMessageRepo,
            emailJobRepo,
            new EmailJobQueue(),
            Options.Create(new DocumentStorageOptions()),
            notificationService,
            logger ?? Mock.Of<ILogger<CommitteeController>>()
        );

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, nameId), new Claim(IdentityProviderClaim, idp) },
                    "Test"
                )
            ),
        };
        if (serviceProvider != null)
            httpContext.RequestServices = serviceProvider;

        c.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return c;
    }

    private static string UniqueId(string nameId, string idp = "google.com") => $"{idp}{nameId}";

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

    private static Committee SampleCommittee(string id = "board") =>
        new Committee
        {
            Id = id,
            DisplayName = "Board",
            Description = "Oversees COHAD operations.",
            CommitteeEmail = "board@cohad.org",
            DisplayOrder = 1,
            ManagementRole = User.Role.Board,
            Members = new List<CommitteeMember>
            {
                new CommitteeMember
                {
                    Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                    ResidentId = AliceResidentId,
                    Title = "President",
                    Bio = "Longtime resident.",
                    ReceivesForwardedEmail = true,
                    DisplayOrder = 1,
                },
                new CommitteeMember
                {
                    Id = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                    ResidentId = BobResidentId,
                    Title = "Treasurer",
                    Bio = "Manages budget.",
                    ReceivesForwardedEmail = true,
                    DisplayOrder = 2,
                },
            },
        };

    private static IUserRepository AdminUserRepo(string nameId = "u1", string idp = "google.com")
    {
        var uniqueId = idp + nameId;
        var mock = new Mock<IUserRepository>();
        mock.Setup(r => r.GetByUniqueIdAsync(uniqueId))
            .ReturnsAsync(
                new User
                {
                    UniqueId = uniqueId,
                    NameIdentifier = nameId,
                    IdentityProvider = idp,
                    GivenName = "Admin",
                    Surname = "User",
                    Roles = new List<User.Role> { User.Role.Administrator },
                }
            );
        return mock.Object;
    }

    private static IUserRepository RoleUserRepo(User.Role role, string nameId = "u1", string idp = "google.com")
    {
        var uniqueId = idp + nameId;
        var mock = new Mock<IUserRepository>();
        mock.Setup(r => r.GetByUniqueIdAsync(uniqueId))
            .ReturnsAsync(
                new User
                {
                    UniqueId = uniqueId,
                    NameIdentifier = nameId,
                    IdentityProvider = idp,
                    GivenName = "Role",
                    Surname = "User",
                    Roles = new List<User.Role> { role },
                }
            );
        return mock.Object;
    }

    private static IFormFile CreateFormFile(string fieldName, string fileName, byte[] content = null)
    {
        content ??= new byte[] { 0x89, 0x50, 0x4E, 0x47 }; // PNG magic bytes
        var ms = new MemoryStream(content);
        return new FormFile(ms, 0, ms.Length, fieldName, fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/jpeg",
        };
    }

    // ──────────────────────────────────────────────
    // Public endpoints
    // ──────────────────────────────────────────────

    [Fact]
    public async Task GetAll_returns_committees_ordered_by_displayOrder()
    {
        var committees = new List<Committee>
        {
            new Committee
            {
                Id = "social",
                DisplayName = "Social",
                DisplayOrder = 3,
                Members = new(),
            },
            new Committee
            {
                Id = "board",
                DisplayName = "Board",
                DisplayOrder = 1,
                Members = new(),
            },
        };
        var mockRepo = new Mock<ICommitteeRepository>();
        mockRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(committees);

        var c = CreateController(committeeRepo: mockRepo.Object);
        var result = await c.GetAll();

        var file = Assert.IsType<FileContentResult>(result);
        var cards = JsonSerializer.Deserialize<List<CommitteeCard>>(file.FileContents, WebJsonOptions);
        Assert.Equal(2, cards.Count);
        Assert.Equal("Board", cards[0].DisplayName);
        Assert.Equal("Social", cards[1].DisplayName);
    }

    [Fact]
    public async Task GetAll_sets_public_cache_control_and_etag()
    {
        var mockRepo = new Mock<ICommitteeRepository>();
        mockRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Committee>());

        var c = CreateController(committeeRepo: mockRepo.Object);
        await c.GetAll();

        Assert.Equal("public, no-cache", c.Response.Headers.CacheControl.ToString());
        Assert.False(string.IsNullOrEmpty(c.Response.Headers.ETag.ToString()));
    }

    [Fact]
    public async Task GetAll_returns_304_when_etag_matches()
    {
        var mockRepo = new Mock<ICommitteeRepository>();
        mockRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Committee>());

        var cache = new CommitteeListCache(mockRepo.Object, new MemoryCache(new MemoryCacheOptions()), WebJsonOptions);
        var c = CreateController(committeeRepo: mockRepo.Object, cache: cache);

        // First request — get the ETag
        var first = await c.GetAll();
        var etag = c.Response.Headers.ETag.ToString();

        // Second request with matching If-None-Match
        c = CreateController(committeeRepo: mockRepo.Object, cache: cache);
        c.Request.Headers["If-None-Match"] = etag;
        var second = await c.GetAll();

        Assert.IsType<StatusCodeResult>(second);
        Assert.Equal(304, ((StatusCodeResult)second).StatusCode);
    }

    // ──────────────────────────────────────────────
    // Admin Update — photo handling
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Update_saves_committee_with_no_photos()
    {
        var committee = SampleCommittee();
        var mockRepo = new Mock<ICommitteeRepository>();
        mockRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);
        mockRepo.Setup(r => r.UpsertAsync(It.IsAny<Committee>())).ReturnsAsync((Committee c) => c);

        var payload = JsonSerializer.Serialize(
            new CommitteeUpdateRequest
            {
                Description = "Updated description",
                DisplayOrder = 1,
                Members = new List<CommitteeMemberUpdate>
                {
                    new()
                    {
                        Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                        ResidentId = AliceResidentId,
                        DisplayOrder = 1,
                    },
                    new()
                    {
                        Id = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                        ResidentId = BobResidentId,
                        DisplayOrder = 2,
                    },
                },
            },
            WebJsonOptions
        );

        var c = CreateController(committeeRepo: mockRepo.Object);
        var result = await c.Update("board", payload, new List<IFormFile>());

        var ok = Assert.IsType<OkObjectResult>(result);
        var admin = Assert.IsType<CommitteeAdmin>(ok.Value);
        Assert.Equal("Updated description", admin.Description);
        Assert.Equal("Alice", admin.Members[0].DisplayName);
        mockRepo.Verify(r => r.UpsertAsync(It.IsAny<Committee>()), Times.Once);
    }

    [Fact]
    public async Task Update_handles_single_photo_upload()
    {
        var committee = SampleCommittee();
        var mockRepo = new Mock<ICommitteeRepository>();
        mockRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);
        mockRepo.Setup(r => r.UpsertAsync(It.IsAny<Committee>())).ReturnsAsync((Committee c) => c);

        var payload = JsonSerializer.Serialize(
            new CommitteeUpdateRequest
            {
                Description = committee.Description,
                DisplayOrder = 1,
                Members = new List<CommitteeMemberUpdate>
                {
                    new()
                    {
                        Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                        ResidentId = AliceResidentId,
                        DisplayOrder = 1,
                    },
                    new()
                    {
                        Id = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                        ResidentId = BobResidentId,
                        DisplayOrder = 2,
                    },
                },
            },
            WebJsonOptions
        );

        // Simulates the frontend: form field name is "photos", file name is "photo-{memberId}.jpg"
        var photo = CreateFormFile("photos", "photo-aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa.jpg");

        var c = CreateController(committeeRepo: mockRepo.Object);
        var result = await c.Update("board", payload, new List<IFormFile> { photo });

        var ok = Assert.IsType<OkObjectResult>(result);
        var admin = Assert.IsType<CommitteeAdmin>(ok.Value);
        Assert.True(admin.Members.First(m => m.Id == Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")).HasPhoto);
    }

    [Fact]
    public async Task Update_handles_multiple_photo_uploads_with_same_field_name()
    {
        var committee = SampleCommittee();
        var mockRepo = new Mock<ICommitteeRepository>();
        mockRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);
        mockRepo.Setup(r => r.UpsertAsync(It.IsAny<Committee>())).ReturnsAsync((Committee c) => c);

        var payload = JsonSerializer.Serialize(
            new CommitteeUpdateRequest
            {
                Description = committee.Description,
                DisplayOrder = 1,
                Members = new List<CommitteeMemberUpdate>
                {
                    new()
                    {
                        Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                        ResidentId = AliceResidentId,
                        DisplayOrder = 1,
                    },
                    new()
                    {
                        Id = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                        ResidentId = BobResidentId,
                        DisplayOrder = 2,
                    },
                },
            },
            WebJsonOptions
        );

        // Both photos use the same form field name "photos" — this was the original bug
        var photo1 = CreateFormFile("photos", "photo-aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa.jpg");
        var photo2 = CreateFormFile("photos", "photo-bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb.jpg");

        var c = CreateController(committeeRepo: mockRepo.Object);
        var result = await c.Update("board", payload, new List<IFormFile> { photo1, photo2 });

        var ok = Assert.IsType<OkObjectResult>(result);
        var admin = Assert.IsType<CommitteeAdmin>(ok.Value);
        Assert.True(admin.Members[0].HasPhoto);
        Assert.True(admin.Members[1].HasPhoto);
    }

    [Fact]
    public async Task Update_rejects_photo_without_extension()
    {
        var committee = SampleCommittee();
        var mockRepo = new Mock<ICommitteeRepository>();
        mockRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);

        var payload = JsonSerializer.Serialize(
            new CommitteeUpdateRequest
            {
                Description = committee.Description,
                DisplayOrder = 1,
                Members = new List<CommitteeMemberUpdate>
                {
                    new()
                    {
                        Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                        ResidentId = AliceResidentId,
                        DisplayOrder = 1,
                    },
                },
            },
            WebJsonOptions
        );

        // No extension on filename — the second bug we fixed
        var photo = CreateFormFile("photos", "photo-aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

        var c = CreateController(committeeRepo: mockRepo.Object);
        var result = await c.Update("board", payload, new List<IFormFile> { photo });

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Unsupported photo format", bad.Value.ToString());
    }

    [Fact]
    public async Task Update_rejects_unsupported_photo_extension()
    {
        var committee = SampleCommittee();
        var mockRepo = new Mock<ICommitteeRepository>();
        mockRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);

        var payload = JsonSerializer.Serialize(
            new CommitteeUpdateRequest
            {
                Description = committee.Description,
                DisplayOrder = 1,
                Members = new List<CommitteeMemberUpdate>
                {
                    new()
                    {
                        Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                        ResidentId = AliceResidentId,
                        DisplayOrder = 1,
                    },
                },
            },
            WebJsonOptions
        );

        var photo = CreateFormFile("photos", "photo-aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa.gif");

        var c = CreateController(committeeRepo: mockRepo.Object);
        var result = await c.Update("board", payload, new List<IFormFile> { photo });

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains(".gif", bad.Value.ToString());
    }

    [Fact]
    public async Task Update_returns_NotFound_for_missing_committee()
    {
        var mockRepo = new Mock<ICommitteeRepository>();
        mockRepo.Setup(r => r.GetByIdAsync("missing")).ReturnsAsync((Committee)null);

        var payload = JsonSerializer.Serialize(
            new CommitteeUpdateRequest { Description = "test", Members = new() },
            WebJsonOptions
        );

        var c = CreateController(committeeRepo: mockRepo.Object);
        var result = await c.Update("missing", payload, new List<IFormFile>());

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Update_returns_BadRequest_for_invalid_json()
    {
        var committee = SampleCommittee();
        var mockRepo = new Mock<ICommitteeRepository>();
        mockRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);

        var c = CreateController(committeeRepo: mockRepo.Object);
        var result = await c.Update("board", "not valid json {{{", new List<IFormFile>());

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Invalid JSON", bad.Value.ToString());
    }

    [Fact]
    public async Task Update_invalidates_cache()
    {
        var committee = SampleCommittee();
        var mockRepo = new Mock<ICommitteeRepository>();
        mockRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Committee> { committee });
        mockRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);
        mockRepo.Setup(r => r.UpsertAsync(It.IsAny<Committee>())).ReturnsAsync((Committee c) => c);

        var memCache = new MemoryCache(new MemoryCacheOptions());
        var cache = new CommitteeListCache(mockRepo.Object, memCache, WebJsonOptions);

        // Prime the cache
        var c1 = CreateController(committeeRepo: mockRepo.Object, cache: cache);
        await c1.GetAll();
        var etag1 = c1.Response.Headers.ETag.ToString();

        // Update the committee (modifies description)
        committee.Description = "Changed";
        var payload = JsonSerializer.Serialize(
            new CommitteeUpdateRequest
            {
                Description = "Changed",
                DisplayOrder = 1,
                Members = new List<CommitteeMemberUpdate>
                {
                    new()
                    {
                        Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                        ResidentId = AliceResidentId,
                        DisplayOrder = 1,
                    },
                },
            },
            WebJsonOptions
        );

        var c2 = CreateController(committeeRepo: mockRepo.Object, cache: cache);
        await c2.Update("board", payload, new List<IFormFile>());

        // Fetch again — ETag should be different since cache was invalidated
        var c3 = CreateController(committeeRepo: mockRepo.Object, cache: cache);
        await c3.GetAll();
        var etag2 = c3.Response.Headers.ETag.ToString();

        Assert.NotEqual(etag1, etag2);
    }

    [Fact]
    public async Task Cache_returns_independent_copies()
    {
        var committee = SampleCommittee();
        var mockRepo = new Mock<ICommitteeRepository>();
        mockRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Committee> { committee });

        var cache = new CommitteeListCache(mockRepo.Object, new MemoryCache(new MemoryCacheOptions()), WebJsonOptions);

        var first = await cache.GetAllAsync();
        Assert.Equal(2, first[0].Members.Count);

        // Mutate the returned list (simulates what RemoveMember does)
        first[0].Members.RemoveAt(1);
        Assert.Single(first[0].Members);

        // Second read should still have both members
        var second = await cache.GetAllAsync();
        Assert.Equal(2, second[0].Members.Count);
    }

    [Fact]
    public async Task Update_deletes_photos_for_removed_members()
    {
        var committee = SampleCommittee();
        committee.Members[1].PhotoBlobPath = "committees/board/bbbbbbbb.jpg";
        committee.Members[1].PhotoContentType = "image/jpeg";

        var mockRepo = new Mock<ICommitteeRepository>();
        mockRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);
        mockRepo.Setup(r => r.UpsertAsync(It.IsAny<Committee>())).ReturnsAsync((Committee c) => c);

        var mockFileStore = new Mock<IDocumentFileStore>();
        mockFileStore.Setup(s => s.DeleteAsync(It.IsAny<string>())).Returns(Task.CompletedTask);

        // Only include Alice in update — Bob is removed
        var payload = JsonSerializer.Serialize(
            new CommitteeUpdateRequest
            {
                Description = committee.Description,
                DisplayOrder = 1,
                Members = new List<CommitteeMemberUpdate>
                {
                    new()
                    {
                        Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                        ResidentId = AliceResidentId,
                        DisplayOrder = 1,
                    },
                },
            },
            WebJsonOptions
        );

        var c = CreateController(committeeRepo: mockRepo.Object, fileStore: mockFileStore.Object);
        await c.Update("board", payload, new List<IFormFile>());

        mockFileStore.Verify(s => s.DeleteAsync("committees/board/bbbbbbbb.jpg"), Times.Once);
    }

    [Fact]
    public async Task Update_deletes_old_blob_when_photo_extension_changes()
    {
        var committee = SampleCommittee();
        var memberId = committee.Members[0].Id;
        committee.Members[0].PhotoBlobPath = $"committees/board/{memberId:D}.jpg";
        committee.Members[0].PhotoContentType = "image/jpeg";

        var mockRepo = new Mock<ICommitteeRepository>();
        mockRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);
        mockRepo.Setup(r => r.UpsertAsync(It.IsAny<Committee>())).ReturnsAsync((Committee c) => c);

        var mockFileStore = new Mock<IDocumentFileStore>();
        mockFileStore.Setup(s => s.DeleteAsync(It.IsAny<string>())).Returns(Task.CompletedTask);

        // Upload helper returns a .png path (different from existing .jpg)
        var mockUpload = new Mock<IImageUploadHelper>();
        mockUpload
            .Setup(h =>
                h.ConvertAndUploadAsync(
                    It.IsAny<IFormFile>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string>()
                )
            )
            .ReturnsAsync(
                new ImageUploadResult($"committees/board/{memberId:D}.png", $"{memberId:D}.png", "image/png", 1234)
            );

        var payload = JsonSerializer.Serialize(
            new CommitteeUpdateRequest
            {
                Description = committee.Description,
                DisplayOrder = 1,
                Members = new List<CommitteeMemberUpdate>
                {
                    new()
                    {
                        Id = memberId,
                        ResidentId = AliceResidentId,
                        DisplayOrder = 1,
                    },
                    new()
                    {
                        Id = committee.Members[1].Id,
                        ResidentId = BobResidentId,
                        DisplayOrder = 2,
                    },
                },
            },
            WebJsonOptions
        );

        var photo = CreateFormFile("photos", $"photo-{memberId:D}.png");

        var c = CreateController(
            committeeRepo: mockRepo.Object,
            fileStore: mockFileStore.Object,
            imageUploadHelper: mockUpload.Object
        );
        var result = await c.Update("board", payload, new List<IFormFile> { photo });

        Assert.IsType<OkObjectResult>(result);
        mockFileStore.Verify(s => s.DeleteAsync($"committees/board/{memberId:D}.jpg"), Times.Once);
    }

    [Fact]
    public async Task Update_adds_new_member_with_generated_id()
    {
        var committee = SampleCommittee();
        var mockRepo = new Mock<ICommitteeRepository>();
        mockRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);

        Committee saved = null;
        mockRepo
            .Setup(r => r.UpsertAsync(It.IsAny<Committee>()))
            .Callback<Committee>(c => saved = c)
            .ReturnsAsync((Committee c) => c);

        var payload = JsonSerializer.Serialize(
            new CommitteeUpdateRequest
            {
                Description = committee.Description,
                DisplayOrder = 1,
                Members = new List<CommitteeMemberUpdate>
                {
                    new()
                    {
                        Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                        ResidentId = AliceResidentId,
                        DisplayOrder = 1,
                    },
                    // New member — Id is empty Guid, should get a generated ID
                    new()
                    {
                        Id = Guid.Empty,
                        ResidentId = NewPersonResidentId,
                        DisplayOrder = 3,
                    },
                },
            },
            WebJsonOptions
        );

        var c = CreateController(committeeRepo: mockRepo.Object);
        await c.Update("board", payload, new List<IFormFile>());

        Assert.NotNull(saved);
        Assert.Equal(2, saved.Members.Count);
        var newMember = saved.Members.First(m => m.ResidentId == NewPersonResidentId);
        Assert.NotEqual(Guid.Empty, newMember.Id);
    }

    [Fact]
    public async Task Update_succeeds_when_new_member_has_no_homeId()
    {
        var committee = SampleCommittee();
        var mockRepo = new Mock<ICommitteeRepository>();
        mockRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);
        mockRepo.Setup(r => r.UpsertAsync(It.IsAny<Committee>())).ReturnsAsync((Committee c) => c);

        // Simulates what the frontend sends: full CommitteeAdmin JSON with a new member
        var json = $$"""
            {
                "id": "board",
                "displayName": "Board",
                "description": "Oversees COHAD operations.",
                "committeeEmail": "board@cohad.org",
                "displayOrder": 1,
                "members": [
                    {
                        "id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                        "residentId": "{{AliceResidentId:D}}",
                        "receivesForwardedEmail": true,
                        "photoOffsetY": 50,
                        "displayOrder": 1
                    },
                    {
                        "residentId": "{{NewPersonResidentId:D}}",
                        "receivesForwardedEmail": true,
                        "photoOffsetY": 50,
                        "displayOrder": 3
                    }
                ]
            }
            """;

        var c = CreateController(committeeRepo: mockRepo.Object);
        var result = await c.Update("board", json, new List<IFormFile>());

        Assert.IsType<OkObjectResult>(result);
    }

    // ──────────────────────────────────────────────
    // Admin — remove member
    // ──────────────────────────────────────────────

    [Fact]
    public async Task RemoveMember_returns_NoContent_and_deletes_photo()
    {
        var committee = SampleCommittee();
        var memberId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        committee.Members[0].PhotoBlobPath = "committees/board/aaaaaaaa.jpg";

        var mockRepo = new Mock<ICommitteeRepository>();
        mockRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);
        mockRepo.Setup(r => r.UpsertAsync(It.IsAny<Committee>())).ReturnsAsync((Committee c) => c);

        var mockFileStore = new Mock<IDocumentFileStore>();
        mockFileStore.Setup(s => s.DeleteAsync(It.IsAny<string>())).Returns(Task.CompletedTask);

        var c = CreateController(committeeRepo: mockRepo.Object, fileStore: mockFileStore.Object);
        var result = await c.RemoveMember("board", memberId);

        Assert.IsType<NoContentResult>(result);
        mockFileStore.Verify(s => s.DeleteAsync("committees/board/aaaaaaaa.jpg"), Times.Once);
        mockRepo.Verify(
            r =>
                r.UpsertAsync(
                    It.Is<Committee>(comm => comm.Members.Count == 1 && comm.Members[0].ResidentId == BobResidentId)
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task RemoveMember_returns_NotFound_for_missing_member()
    {
        var committee = SampleCommittee();
        var mockRepo = new Mock<ICommitteeRepository>();
        mockRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);

        var c = CreateController(committeeRepo: mockRepo.Object);
        var result = await c.RemoveMember("board", Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result);
    }

    // ──────────────────────────────────────────────
    // Photo URL generation (CommitteeMemberCard)
    // ──────────────────────────────────────────────

    [Fact]
    public void PhotoDownloadUrl_uses_singular_controller_route()
    {
        var member = new CommitteeMember
        {
            Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            ResidentId = AliceResidentId,
            PhotoBlobPath = "committees/board/aaaaaaaa.jpg",
            PhotoContentType = "image/jpeg",
        };

        var card = CommitteeMemberCard.FromStorageModel(member, "board", AliceResident());

        Assert.True(card.HasPhoto);
        Assert.StartsWith("/api/committee/", card.PhotoDownloadUrl);
        Assert.Equal("/api/committee/board/members/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/photo", card.PhotoDownloadUrl);
    }

    [Fact]
    public void PhotoOffsetY_defaults_to_50_and_round_trips_through_card()
    {
        var member = new CommitteeMember
        {
            Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            ResidentId = AliceResidentId,
            PhotoBlobPath = "committees/board/aaaaaaaa.jpg",
        };

        // Default is 50
        Assert.Equal(50, member.PhotoOffsetY);

        var card = CommitteeMemberCard.FromStorageModel(member, "board", AliceResident());
        Assert.Equal(50, card.PhotoOffsetY);

        // Custom value
        member.PhotoOffsetY = 25;
        var card2 = CommitteeMemberCard.FromStorageModel(member, "board", AliceResident());
        Assert.Equal(25, card2.PhotoOffsetY);

        // Admin model round-trip
        var admin = CommitteeMemberAdmin.FromStorageModel(member, AliceResident());
        Assert.Equal(25, admin.PhotoOffsetY);
    }

    [Fact]
    public async Task Update_clamps_photoOffsetY_to_valid_range()
    {
        var committee = SampleCommittee();
        var mockRepo = new Mock<ICommitteeRepository>();
        mockRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);

        Committee saved = null;
        mockRepo
            .Setup(r => r.UpsertAsync(It.IsAny<Committee>()))
            .Callback<Committee>(c => saved = c)
            .ReturnsAsync((Committee c) => c);

        var payload = JsonSerializer.Serialize(
            new CommitteeUpdateRequest
            {
                Description = committee.Description,
                DisplayOrder = 1,
                Members = new List<CommitteeMemberUpdate>
                {
                    new()
                    {
                        Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                        ResidentId = AliceResidentId,
                        PhotoOffsetY = -10,
                        DisplayOrder = 1,
                    },
                    new()
                    {
                        Id = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                        ResidentId = BobResidentId,
                        PhotoOffsetY = 200,
                        DisplayOrder = 2,
                    },
                },
            },
            WebJsonOptions
        );

        var c = CreateController(committeeRepo: mockRepo.Object);
        await c.Update("board", payload, new List<IFormFile>());

        Assert.NotNull(saved);
        Assert.Equal(0, saved.Members[0].PhotoOffsetY); // clamped from -10
        Assert.Equal(100, saved.Members[1].PhotoOffsetY); // clamped from 200
    }

    [Fact]
    public void PhotoDownloadUrl_is_null_when_no_photo()
    {
        var member = new CommitteeMember
        {
            Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            ResidentId = AliceResidentId,
            PhotoBlobPath = null,
        };

        var card = CommitteeMemberCard.FromStorageModel(member, "board", AliceResident());

        Assert.False(card.HasPhoto);
        Assert.Null(card.PhotoDownloadUrl);
    }

    // ──────────────────────────────────────────────
    // Member photo download
    // ──────────────────────────────────────────────

    [Fact]
    public async Task DownloadMemberPhoto_returns_NotFound_when_no_photo()
    {
        var committee = SampleCommittee();
        var mockRepo = new Mock<ICommitteeRepository>();
        mockRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);

        var c = CreateController(committeeRepo: mockRepo.Object);
        var result = await c.DownloadMemberPhoto("board", Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task DownloadMemberPhoto_returns_file_with_cache_headers()
    {
        var committee = SampleCommittee();
        committee.Members[0].PhotoBlobPath = "committees/board/aaaaaaaa.jpg";
        committee.Members[0].PhotoContentType = "image/jpeg";

        var mockRepo = new Mock<ICommitteeRepository>();
        mockRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);

        var mockFileStore = new Mock<IDocumentFileStore>();
        mockFileStore
            .Setup(s => s.DownloadAsync("committees/board/aaaaaaaa.jpg"))
            .ReturnsAsync(
                new DocumentFileResult
                {
                    Stream = new MemoryStream(Encoding.UTF8.GetBytes("fake-image")),
                    ContentType = "image/jpeg",
                    EntityTag = new Microsoft.Net.Http.Headers.EntityTagHeaderValue("\"abc123\""),
                    LastModified = DateTimeOffset.UtcNow,
                }
            );

        var c = CreateController(committeeRepo: mockRepo.Object, fileStore: mockFileStore.Object);
        var result = await c.DownloadMemberPhoto("board", Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("image/jpeg", file.ContentType);
        Assert.Equal("public, no-cache", c.Response.Headers["Cache-Control"].ToString());
    }

    [Fact]
    public async Task DownloadMemberPhoto_returns_NotFound_for_missing_committee()
    {
        var mockRepo = new Mock<ICommitteeRepository>();
        mockRepo.Setup(r => r.GetByIdAsync("missing")).ReturnsAsync((Committee)null);

        var c = CreateController(committeeRepo: mockRepo.Object);
        var result = await c.DownloadMemberPhoto("missing", Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task DownloadMemberPhoto_returns_NotFound_for_missing_blob()
    {
        var committee = SampleCommittee();
        committee.Members[0].PhotoBlobPath = "committees/board/aaaaaaaa.jpg";

        var mockRepo = new Mock<ICommitteeRepository>();
        mockRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);

        var mockFileStore = new Mock<IDocumentFileStore>();
        mockFileStore.Setup(s => s.DownloadAsync(It.IsAny<string>())).ReturnsAsync((DocumentFileResult)null);

        var c = CreateController(committeeRepo: mockRepo.Object, fileStore: mockFileStore.Object);
        var result = await c.DownloadMemberPhoto("board", Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

        Assert.IsType<NotFoundResult>(result);
    }

    // ──────────────────────────────────────────────
    // Forwarding sync (deprecated — endpoint returns 410 Gone)
    // ──────────────────────────────────────────────

    [Fact]
    public async Task SyncForwarding_returns_410_Gone()
    {
        var c = CreateController();
        var result = await c.SyncForwarding("board");

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(410, status.StatusCode);
    }

    // ──────────────────────────────────────────────
    // Audit logging
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Update_writes_audit_log_entry()
    {
        var committee = SampleCommittee();
        var uniqueId = UniqueId("u1");

        var mockRepo = new Mock<ICommitteeRepository>();
        mockRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);
        mockRepo.Setup(r => r.UpsertAsync(It.IsAny<Committee>())).ReturnsAsync((Committee c) => c);

        var mockUsers = new Mock<IUserRepository>();
        mockUsers
            .Setup(r => r.GetByUniqueIdAsync(uniqueId))
            .ReturnsAsync(
                new User
                {
                    UniqueId = uniqueId,
                    GivenName = "Mock",
                    Surname = "Admin",
                    Roles = new List<User.Role> { User.Role.Administrator },
                }
            );

        var mockAudit = new Mock<IAuditLogRepository>();
        mockAudit.Setup(a => a.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var payload = JsonSerializer.Serialize(
            new CommitteeUpdateRequest
            {
                Description = "Updated",
                DisplayOrder = 1,
                Members = new List<CommitteeMemberUpdate>
                {
                    new()
                    {
                        Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                        ResidentId = AliceResidentId,
                        DisplayOrder = 1,
                    },
                },
            },
            WebJsonOptions
        );

        var c = CreateController(
            committeeRepo: mockRepo.Object,
            userRepo: mockUsers.Object,
            auditLogRepo: mockAudit.Object
        );
        await c.Update("board", payload, new List<IFormFile>());

        mockAudit.Verify(
            a =>
                a.AddAsync(
                    It.Is<NewAuditLogEntry>(e =>
                        e.SubjectId == "board"
                        && e.SubjectName == "Board"
                        && e.Action == "Updated committee."
                        && e.UserId == uniqueId
                    )
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task RemoveMember_writes_audit_log_entry()
    {
        var committee = SampleCommittee();
        var uniqueId = UniqueId("u1");

        var mockRepo = new Mock<ICommitteeRepository>();
        mockRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);
        mockRepo.Setup(r => r.UpsertAsync(It.IsAny<Committee>())).ReturnsAsync((Committee c) => c);

        var mockUsers = new Mock<IUserRepository>();
        mockUsers
            .Setup(r => r.GetByUniqueIdAsync(uniqueId))
            .ReturnsAsync(
                new User
                {
                    UniqueId = uniqueId,
                    GivenName = "Mock",
                    Surname = "Admin",
                    Roles = new List<User.Role> { User.Role.Administrator },
                }
            );

        var mockAudit = new Mock<IAuditLogRepository>();
        mockAudit.Setup(a => a.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var c = CreateController(
            committeeRepo: mockRepo.Object,
            userRepo: mockUsers.Object,
            auditLogRepo: mockAudit.Object
        );
        await c.RemoveMember("board", Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

        mockAudit.Verify(
            a =>
                a.AddAsync(
                    It.Is<NewAuditLogEntry>(e =>
                        e.SubjectId == "board" && e.Action.Contains("Alice") && e.Action.Contains("Removed member")
                    )
                ),
            Times.Once
        );
    }

    // ──────────────────────────────────────────────
    // Resident picker + validation telemetry
    // ──────────────────────────────────────────────

    [Fact]
    public async Task GetResidentsForPicker_excludes_children()
    {
        var adult = new Resident
        {
            Id = Guid.NewGuid(),
            GivenName = "Justin",
            Surname = "Adult",
            ResidentType = Resident.Type.Homeowner,
            EmailAddresses = new List<EmailAddress> { new EmailAddress { Address = "justin.adult@example.com" } },
        };
        var child = new Resident
        {
            Id = Guid.NewGuid(),
            GivenName = "Justin",
            Surname = "", // children commonly have no surname/email
            ResidentType = Resident.Type.Child,
            EmailAddresses = new List<EmailAddress>(),
        };

        var residentRepo = new Mock<IResidentRepository>();
        residentRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Resident> { adult, child });

        var c = CreateController(residentRepo: residentRepo.Object);
        var result = await c.GetResidentsForPicker();

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value, WebJsonOptions);
        Assert.Single(((System.Collections.IEnumerable)ok.Value!).Cast<object>());
        Assert.Contains(adult.Id.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(child.Id.ToString(), json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetResidentsForPicker_collapses_internal_whitespace_in_display_name()
    {
        // Stored name with a trailing space on GivenName must not render as "John  Doe".
        var resident = new Resident
        {
            Id = Guid.NewGuid(),
            GivenName = "John ",
            Surname = " Doe",
            ResidentType = Resident.Type.Homeowner,
            EmailAddresses = new List<EmailAddress> { new EmailAddress { Address = "john.doe@example.com" } },
        };

        var residentRepo = new Mock<IResidentRepository>();
        residentRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Resident> { resident });

        var c = CreateController(residentRepo: residentRepo.Object);
        var result = await c.GetResidentsForPicker();

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value, WebJsonOptions);
        Assert.Contains("John Doe", json);
        Assert.DoesNotContain("John  Doe", json);
    }

    [Fact]
    public async Task Update_validation_failure_logs_warning_for_telemetry()
    {
        var committee = SampleCommittee("board");
        var mockRepo = new Mock<ICommitteeRepository>();
        mockRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);

        // A member with no resident selected → the "Each member must reference a valid ResidentId" path.
        var payload = JsonSerializer.Serialize(
            new CommitteeUpdateRequest
            {
                Description = "x",
                Members = new List<CommitteeMemberUpdate>
                {
                    new() { Id = Guid.NewGuid(), ResidentId = Guid.Empty, DisplayOrder = 1 },
                },
            },
            WebJsonOptions
        );

        var mockLogger = new Mock<ILogger<CommitteeController>>();
        var c = CreateController(committeeRepo: mockRepo.Object, logger: mockLogger.Object);

        var result = await c.Update("board", payload, new List<IFormFile>());

        Assert.IsType<BadRequestObjectResult>(result);
        mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("Committee update rejected")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()
            ),
            Times.Once
        );
    }

    // ──────────────────────────────────────────────
    // Role-based committee access
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Admin_can_edit_any_committee()
    {
        var board = SampleCommittee("board");
        board.ManagementRole = User.Role.Board;
        var social = SampleCommittee("social");
        social.ManagementRole = User.Role.SocialCommittee;

        var mockRepo = new Mock<ICommitteeRepository>();
        mockRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Committee> { board, social });

        var c = CreateController(committeeRepo: mockRepo.Object, userRepo: AdminUserRepo());
        var result = await c.GetAllAdmin();

        var ok = Assert.IsType<OkObjectResult>(result);
        var list = Assert.IsAssignableFrom<IList<CommitteeAdmin>>(ok.Value);
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public async Task Committee_role_holder_sees_only_their_committee()
    {
        var board = SampleCommittee("board");
        board.ManagementRole = User.Role.Board;
        var social = SampleCommittee("social");
        social.ManagementRole = User.Role.SocialCommittee;

        var mockRepo = new Mock<ICommitteeRepository>();
        mockRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Committee> { board, social });

        var c = CreateController(committeeRepo: mockRepo.Object, userRepo: RoleUserRepo(User.Role.Board));
        var result = await c.GetAllAdmin();

        var ok = Assert.IsType<OkObjectResult>(result);
        var list = Assert.IsAssignableFrom<IList<CommitteeAdmin>>(ok.Value);
        Assert.Single(list);
        Assert.Equal("board", list[0].Id);
    }

    [Fact]
    public async Task Committee_role_holder_can_update_their_committee()
    {
        var committee = SampleCommittee("board");
        committee.ManagementRole = User.Role.Board;

        var mockRepo = new Mock<ICommitteeRepository>();
        mockRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);
        mockRepo.Setup(r => r.UpsertAsync(It.IsAny<Committee>())).ReturnsAsync((Committee c) => c);

        var payload = JsonSerializer.Serialize(
            new CommitteeUpdateRequest
            {
                Description = "Updated",
                DisplayOrder = 1,
                Members = new List<CommitteeMemberUpdate>(),
            },
            WebJsonOptions
        );

        var c = CreateController(committeeRepo: mockRepo.Object, userRepo: RoleUserRepo(User.Role.Board));
        var result = await c.Update("board", payload, new List<IFormFile>());

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task Committee_role_holder_is_forbidden_from_other_committee()
    {
        var social = SampleCommittee("social");
        social.ManagementRole = User.Role.SocialCommittee;

        var mockRepo = new Mock<ICommitteeRepository>();
        mockRepo.Setup(r => r.GetByIdAsync("social")).ReturnsAsync(social);

        var payload = JsonSerializer.Serialize(
            new CommitteeUpdateRequest
            {
                Description = "Attempted",
                DisplayOrder = 1,
                Members = new List<CommitteeMemberUpdate>(),
            },
            WebJsonOptions
        );

        var c = CreateController(committeeRepo: mockRepo.Object, userRepo: RoleUserRepo(User.Role.Board));
        var result = await c.Update("social", payload, new List<IFormFile>());

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task GetByKeyAdmin_forbidden_for_wrong_role()
    {
        var social = SampleCommittee("social");
        social.ManagementRole = User.Role.SocialCommittee;

        var mockRepo = new Mock<ICommitteeRepository>();
        mockRepo.Setup(r => r.GetByIdAsync("social")).ReturnsAsync(social);

        var c = CreateController(committeeRepo: mockRepo.Object, userRepo: RoleUserRepo(User.Role.Board));
        var result = await c.GetByKeyAdmin("social");

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task Unknown_user_gets_forbidden()
    {
        var committee = SampleCommittee("board");
        committee.ManagementRole = User.Role.Board;

        var mockRepo = new Mock<ICommitteeRepository>();
        mockRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);

        // userRepo that returns null (unknown user)
        var c = CreateController(committeeRepo: mockRepo.Object, userRepo: Mock.Of<IUserRepository>());
        var result = await c.Update("board", "{}", new List<IFormFile>());

        Assert.IsType<ForbidResult>(result);
    }

    // ──────────────────────────────────────────────
    // Forwarding settings endpoints
    // ──────────────────────────────────────────────

    [Fact]
    public async Task GetForwardingSettings_returns_settings()
    {
        var committee = SampleCommittee("board");
        committee.ForwardingEnabled = true;
        committee.ForwardingSenderFilter = ForwardingSenderFilter.DirectoryOnly;
        committee.LastPollUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        committee.LastPollStatus = "Success";

        var mockRepo = new Mock<ICommitteeRepository>();
        mockRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);

        var c = CreateController(committeeRepo: mockRepo.Object);
        var result = await c.GetForwardingSettings("board");

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value);
        var doc = JsonSerializer.Deserialize<JsonElement>(json);
        Assert.True(doc.GetProperty("ForwardingEnabled").GetBoolean());
        Assert.Equal("DirectoryOnly", doc.GetProperty("ForwardingSenderFilter").GetString());
    }

    [Fact]
    public async Task UpdateForwardingSettings_invalid_filter_returns_400()
    {
        var committee = SampleCommittee("board");
        var mockRepo = new Mock<ICommitteeRepository>();
        mockRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);

        var c = CreateController(committeeRepo: mockRepo.Object);
        var result = await c.UpdateForwardingSettings("board", new ForwardingSettingsUpdate
        {
            ForwardingEnabled = false,
            ForwardingSenderFilter = "InvalidValue"
        });

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var json = JsonSerializer.Serialize(bad.Value);
        Assert.Contains("Invalid", json);
    }

    [Fact]
    public async Task UpdateForwardingSettings_enable_without_graph_returns_400()
    {
        var committee = SampleCommittee("board");
        var mockRepo = new Mock<ICommitteeRepository>();
        mockRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);

        // No IGraphMailReader registered in services
        var services = new ServiceCollection().BuildServiceProvider();
        var c = CreateController(committeeRepo: mockRepo.Object, serviceProvider: services);
        var result = await c.UpdateForwardingSettings("board", new ForwardingSettingsUpdate
        {
            ForwardingEnabled = true,
            ForwardingSenderFilter = "All"
        });

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var json = JsonSerializer.Serialize(bad.Value);
        Assert.Contains("Graph API", json);
    }

    [Fact]
    public async Task UpdateForwardingSettings_disable_without_graph_succeeds()
    {
        var committee = SampleCommittee("board");
        committee.ForwardingEnabled = true;
        var mockRepo = new Mock<ICommitteeRepository>();
        mockRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);

        var services = new ServiceCollection().BuildServiceProvider();
        var c = CreateController(committeeRepo: mockRepo.Object, serviceProvider: services);
        var result = await c.UpdateForwardingSettings("board", new ForwardingSettingsUpdate
        {
            ForwardingEnabled = false,
            ForwardingSenderFilter = "All"
        });

        Assert.IsType<OkObjectResult>(result);
    }

    // ──────────────────────────────────────────────
    // Moderation queue endpoints
    // ──────────────────────────────────────────────

    [Fact]
    public async Task GetPendingHeldMessages_aggregates_across_managed_committees_newest_first()
    {
        var board = SampleCommittee("board");
        var garden = SampleCommittee("garden");
        garden.DisplayName = "Garden Club";

        var committeeRepo = new Mock<ICommitteeRepository>();
        committeeRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Committee> { board, garden });

        var older = new HeldMessage
        {
            Id = Guid.NewGuid(),
            CommitteeId = "board",
            SenderEmail = "a@example.com",
            Subject = "Older",
            Status = HeldMessageStatus.Held,
            HeldUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var newer = new HeldMessage
        {
            Id = Guid.NewGuid(),
            CommitteeId = "garden",
            SenderEmail = "b@example.com",
            Subject = "Newer",
            Status = HeldMessageStatus.Held,
            HeldUtc = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        var heldRepo = new Mock<IHeldMessageRepository>();
        heldRepo.Setup(r => r.GetByCommitteeIdAsync("board", It.IsAny<int>(), HeldMessageStatus.Held))
            .ReturnsAsync(new List<HeldMessage> { older });
        heldRepo.Setup(r => r.GetByCommitteeIdAsync("garden", It.IsAny<int>(), HeldMessageStatus.Held))
            .ReturnsAsync(new List<HeldMessage> { newer });

        var c = CreateController(committeeRepo: committeeRepo.Object, heldMessageRepo: heldRepo.Object);
        var result = await c.GetPendingHeldMessages();

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsAssignableFrom<IEnumerable<PendingHeldMessagePresentation>>(ok.Value).ToList();
        Assert.Equal(2, payload.Count);
        Assert.Equal("Newer", payload[0].Subject);
        Assert.Equal("Garden Club", payload[0].CommitteeName);
        Assert.Equal("Older", payload[1].Subject);
        Assert.Equal("Board", payload[1].CommitteeName);
    }

    [Fact]
    public async Task GetPendingHeldMessages_returns_Forbid_when_the_user_record_is_not_found()
    {
        var committeeRepo = new Mock<ICommitteeRepository>();
        committeeRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Committee>());

        var userRepo = new Mock<IUserRepository>();
        userRepo.Setup(r => r.GetByUniqueIdAsync(It.IsAny<string>())).ReturnsAsync((User?)null);

        var c = CreateController(committeeRepo: committeeRepo.Object, userRepo: userRepo.Object);
        var result = await c.GetPendingHeldMessages();

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task GetPendingHeldMessages_returns_empty_when_the_caller_manages_no_committees()
    {
        var committeeRepo = new Mock<ICommitteeRepository>();
        committeeRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Committee>());

        // Strict: with no manageable committees, the held store must never be queried.
        var heldRepo = new Mock<IHeldMessageRepository>(MockBehavior.Strict);

        var c = CreateController(committeeRepo: committeeRepo.Object, heldMessageRepo: heldRepo.Object);
        var result = await c.GetPendingHeldMessages();

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsAssignableFrom<IEnumerable<PendingHeldMessagePresentation>>(ok.Value).ToList();
        Assert.Empty(payload);
    }

    [Fact]
    public async Task GetPendingHeldMessages_excludes_committees_the_caller_cannot_manage()
    {
        // A Garden Club moderator (not an Administrator) sees only Garden Club's held mail.
        var board = SampleCommittee("board"); // ManagementRole = Board
        var garden = SampleCommittee("garden");
        garden.DisplayName = "Garden Club";
        garden.ManagementRole = User.Role.GardenClub;

        var committeeRepo = new Mock<ICommitteeRepository>();
        committeeRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Committee> { board, garden });

        // Strict: a setup only for the manageable committee — querying "board" would fail the test.
        var heldRepo = new Mock<IHeldMessageRepository>(MockBehavior.Strict);
        heldRepo.Setup(r => r.GetByCommitteeIdAsync("garden", It.IsAny<int>(), HeldMessageStatus.Held))
            .ReturnsAsync(new List<HeldMessage>
            {
                new HeldMessage { Id = Guid.NewGuid(), CommitteeId = "garden", Subject = "G", Status = HeldMessageStatus.Held, HeldUtc = DateTime.UtcNow },
            });

        var c = CreateController(
            committeeRepo: committeeRepo.Object,
            heldMessageRepo: heldRepo.Object,
            userRepo: RoleUserRepo(User.Role.GardenClub)
        );
        var result = await c.GetPendingHeldMessages();

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsAssignableFrom<IEnumerable<PendingHeldMessagePresentation>>(ok.Value).ToList();
        Assert.Single(payload);
        Assert.Equal("Garden Club", payload[0].CommitteeName);
    }

    [Fact]
    public async Task ApproveHeldMessage_already_approved_returns_400()
    {
        var committee = SampleCommittee("board");
        var heldId = Guid.NewGuid();
        var held = new HeldMessage
        {
            Id = heldId,
            CommitteeId = "board",
            CommitteeEmail = "board@cohad.org",
            InternetMessageId = "<test@example.com>",
            SenderEmail = "sender@example.com",
            Subject = "Test",
            ReceivedUtc = DateTime.UtcNow,
            HeldUtc = DateTime.UtcNow,
            Status = HeldMessageStatus.Approved,
            ETag = "etag-1"
        };

        var mockCommitteeRepo = new Mock<ICommitteeRepository>();
        mockCommitteeRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);

        var mockHeldRepo = new Mock<IHeldMessageRepository>();
        mockHeldRepo.Setup(r => r.GetByIdAsync(heldId)).ReturnsAsync(held);

        var c = CreateController(committeeRepo: mockCommitteeRepo.Object, heldMessageRepo: mockHeldRepo.Object);
        var result = await c.ApproveHeldMessage("board", heldId);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var json = JsonSerializer.Serialize(bad.Value);
        Assert.Contains("already", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectHeldMessage_already_rejected_returns_400()
    {
        var committee = SampleCommittee("board");
        var heldId = Guid.NewGuid();
        var held = new HeldMessage
        {
            Id = heldId,
            CommitteeId = "board",
            CommitteeEmail = "board@cohad.org",
            InternetMessageId = "<test@example.com>",
            SenderEmail = "sender@example.com",
            Subject = "Test",
            ReceivedUtc = DateTime.UtcNow,
            HeldUtc = DateTime.UtcNow,
            Status = HeldMessageStatus.Rejected,
            ETag = "etag-1"
        };

        var mockCommitteeRepo = new Mock<ICommitteeRepository>();
        mockCommitteeRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);

        var mockHeldRepo = new Mock<IHeldMessageRepository>();
        mockHeldRepo.Setup(r => r.GetByIdAsync(heldId)).ReturnsAsync(held);

        var c = CreateController(committeeRepo: mockCommitteeRepo.Object, heldMessageRepo: mockHeldRepo.Object);
        var result = await c.RejectHeldMessage("board", heldId);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var json = JsonSerializer.Serialize(bad.Value);
        Assert.Contains("already", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApproveHeldMessage_etag_conflict_returns_409()
    {
        var committee = SampleCommittee("board");
        var heldId = Guid.NewGuid();
        var held = new HeldMessage
        {
            Id = heldId,
            CommitteeId = "board",
            CommitteeEmail = "board@cohad.org",
            InternetMessageId = "<test@example.com>",
            SenderEmail = "sender@example.com",
            Subject = "Test",
            ReceivedUtc = DateTime.UtcNow,
            HeldUtc = DateTime.UtcNow,
            Status = HeldMessageStatus.Held,
            ETag = "etag-1"
        };

        var mockCommitteeRepo = new Mock<ICommitteeRepository>();
        mockCommitteeRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);

        var mockHeldRepo = new Mock<IHeldMessageRepository>();
        mockHeldRepo.Setup(r => r.GetByIdAsync(heldId)).ReturnsAsync(held);
        mockHeldRepo.Setup(r => r.UpdateAsync(It.IsAny<HeldMessage>()))
            .ThrowsAsync(new InvalidOperationException("HeldMessage was modified by another process."));

        var c = CreateController(committeeRepo: mockCommitteeRepo.Object, heldMessageRepo: mockHeldRepo.Object);
        var result = await c.ApproveHeldMessage("board", heldId);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        var json = JsonSerializer.Serialize(conflict.Value);
        Assert.Contains("already actioned", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectHeldMessage_etag_conflict_returns_409()
    {
        var committee = SampleCommittee("board");
        var heldId = Guid.NewGuid();
        var held = new HeldMessage
        {
            Id = heldId,
            CommitteeId = "board",
            CommitteeEmail = "board@cohad.org",
            InternetMessageId = "<test@example.com>",
            SenderEmail = "sender@example.com",
            Subject = "Test",
            ReceivedUtc = DateTime.UtcNow,
            HeldUtc = DateTime.UtcNow,
            Status = HeldMessageStatus.Held,
            ETag = "etag-1"
        };

        var mockCommitteeRepo = new Mock<ICommitteeRepository>();
        mockCommitteeRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);

        var mockHeldRepo = new Mock<IHeldMessageRepository>();
        mockHeldRepo.Setup(r => r.GetByIdAsync(heldId)).ReturnsAsync(held);
        mockHeldRepo.Setup(r => r.UpdateAsync(It.IsAny<HeldMessage>()))
            .ThrowsAsync(new InvalidOperationException("HeldMessage was modified by another process."));

        var c = CreateController(committeeRepo: mockCommitteeRepo.Object, heldMessageRepo: mockHeldRepo.Object);
        var result = await c.RejectHeldMessage("board", heldId);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        var json = JsonSerializer.Serialize(conflict.Value);
        Assert.Contains("already actioned", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApproveHeldMessage_no_recipients_returns_400()
    {
        // Committee with no members that receive forwarded email
        var committee = SampleCommittee("board");
        foreach (var m in committee.Members) m.ReceivesForwardedEmail = false;

        var heldId = Guid.NewGuid();
        var held = new HeldMessage
        {
            Id = heldId,
            CommitteeId = "board",
            CommitteeEmail = "board@cohad.org",
            InternetMessageId = "<test@example.com>",
            SenderEmail = "sender@example.com",
            Subject = "Test",
            ReceivedUtc = DateTime.UtcNow,
            HeldUtc = DateTime.UtcNow,
            Status = HeldMessageStatus.Held,
            ETag = "etag-1"
        };

        var mockCommitteeRepo = new Mock<ICommitteeRepository>();
        mockCommitteeRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);

        var mockHeldRepo = new Mock<IHeldMessageRepository>();
        mockHeldRepo.Setup(r => r.GetByIdAsync(heldId)).ReturnsAsync(held);

        var c = CreateController(committeeRepo: mockCommitteeRepo.Object, heldMessageRepo: mockHeldRepo.Object);
        var result = await c.ApproveHeldMessage("board", heldId);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var json = JsonSerializer.Serialize(bad.Value);
        Assert.Contains("No forwarding recipients", json);
    }

    [Fact]
    public async Task ApproveHeldMessage_forwarding_job_records_the_sender_as_the_author_not_the_committee()
    {
        var committee = SampleCommittee("board");

        var heldId = Guid.NewGuid();
        var held = new HeldMessage
        {
            Id = heldId,
            CommitteeId = "board",
            CommitteeEmail = "board@cohad.org",
            InternetMessageId = "<test@example.com>",
            SenderEmail = "jane@example.com",
            SenderName = "Jane Doe",
            Subject = "Request to repaint front door",
            ReceivedUtc = DateTime.UtcNow,
            HeldUtc = DateTime.UtcNow,
            Status = HeldMessageStatus.Held,
            ETag = "etag-1"
        };

        var mockCommitteeRepo = new Mock<ICommitteeRepository>();
        mockCommitteeRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);

        var mockHeldRepo = new Mock<IHeldMessageRepository>();
        mockHeldRepo.Setup(r => r.GetByIdAsync(heldId)).ReturnsAsync(held);
        mockHeldRepo.Setup(r => r.UpdateAsync(It.IsAny<HeldMessage>())).Returns(Task.CompletedTask);

        var mockFileStore = new Mock<IDocumentFileStore>();
        mockFileStore.Setup(f => f.UploadAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        EmailJob? created = null;
        var mockEmailJobRepo = new Mock<IEmailJobRepository>();
        mockEmailJobRepo.Setup(r => r.AddAsync(It.IsAny<EmailJob>()))
            .Callback<EmailJob>(j => created = j)
            .Returns(Task.CompletedTask);

        var c = CreateController(
            committeeRepo: mockCommitteeRepo.Object,
            fileStore: mockFileStore.Object,
            heldMessageRepo: mockHeldRepo.Object,
            emailJobRepo: mockEmailJobRepo.Object,
            // No IGraphMailReader registered: the original body is unavailable, which the approve
            // path tolerates and which keeps this test focused on the job's From/To fields.
            serviceProvider: new ServiceCollection().BuildServiceProvider()
        );
        var result = await c.ApproveHeldMessage("board", heldId);

        Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(created);
        // The approve path must describe the message exactly as the poller does.
        Assert.Equal("board@cohad.org", created!.FromEmail);
        Assert.Equal("jane@example.com", created.OriginalSenderEmail);
        Assert.Equal("Jane Doe", created.OriginalSenderDisplay);
        Assert.Equal("jane@example.com", created.ReplyToEmail);
        Assert.Equal("Board forwarding members", created.ToDisplay);
    }

    [Fact]
    public async Task GetHeldMessages_returns_held_messages_for_committee()
    {
        var committee = SampleCommittee("board");
        var heldId = Guid.NewGuid();
        var held = new HeldMessage
        {
            Id = heldId,
            CommitteeId = "board",
            CommitteeEmail = "board@cohad.org",
            InternetMessageId = "<test@example.com>",
            SenderEmail = "sender@example.com",
            SenderName = "Sender",
            Subject = "Test Subject",
            ReceivedUtc = DateTime.UtcNow.AddMinutes(-10),
            HeldUtc = DateTime.UtcNow.AddMinutes(-5),
            Status = HeldMessageStatus.Held,
        };

        var mockCommitteeRepo = new Mock<ICommitteeRepository>();
        mockCommitteeRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);

        var mockHeldRepo = new Mock<IHeldMessageRepository>();
        mockHeldRepo.Setup(r => r.GetByCommitteeIdAsync("board", 50))
            .ReturnsAsync(new List<HeldMessage> { held });

        var c = CreateController(committeeRepo: mockCommitteeRepo.Object, heldMessageRepo: mockHeldRepo.Object);
        var result = await c.GetHeldMessages("board");

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value, WebJsonOptions);
        Assert.Contains("sender@example.com", json);
        Assert.Contains("Test Subject", json);
    }

    [Fact]
    public async Task RejectHeldMessage_success_returns_rejected_status()
    {
        var committee = SampleCommittee("board");
        var heldId = Guid.NewGuid();
        var held = new HeldMessage
        {
            Id = heldId,
            CommitteeId = "board",
            CommitteeEmail = "board@cohad.org",
            InternetMessageId = "<test@example.com>",
            SenderEmail = "sender@example.com",
            Subject = "Test",
            ReceivedUtc = DateTime.UtcNow,
            HeldUtc = DateTime.UtcNow,
            Status = HeldMessageStatus.Held,
            ETag = "etag-1"
        };

        var mockCommitteeRepo = new Mock<ICommitteeRepository>();
        mockCommitteeRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);

        var mockHeldRepo = new Mock<IHeldMessageRepository>();
        mockHeldRepo.Setup(r => r.GetByIdAsync(heldId)).ReturnsAsync(held);

        var c = CreateController(committeeRepo: mockCommitteeRepo.Object, heldMessageRepo: mockHeldRepo.Object);
        var result = await c.RejectHeldMessage("board", heldId);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value, WebJsonOptions);
        Assert.Contains("Rejected", json);

        // Verify UpdateAsync was called
        mockHeldRepo.Verify(r => r.UpdateAsync(It.Is<HeldMessage>(h => h.Status == HeldMessageStatus.Rejected)), Times.Once);
    }

    [Fact]
    public async Task RejectHeldMessage_resolves_held_message_notification()
    {
        var committee = SampleCommittee("board");
        var heldId = Guid.NewGuid();
        var held = new HeldMessage
        {
            Id = heldId,
            CommitteeId = "board",
            CommitteeEmail = "board@cohad.org",
            InternetMessageId = "<test@example.com>",
            SenderEmail = "sender@example.com",
            Subject = "Test",
            Status = HeldMessageStatus.Held,
            ETag = "etag-1",
        };

        var mockCommitteeRepo = new Mock<ICommitteeRepository>();
        mockCommitteeRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);
        var mockHeldRepo = new Mock<IHeldMessageRepository>();
        mockHeldRepo.Setup(r => r.GetByIdAsync(heldId)).ReturnsAsync(held);
        var notifications = new Mock<INotificationService>();

        var c = CreateController(
            committeeRepo: mockCommitteeRepo.Object,
            heldMessageRepo: mockHeldRepo.Object,
            notificationService: notifications.Object
        );
        await c.RejectHeldMessage("board", heldId);

        notifications.Verify(s => s.ResolveAsync(
            NotificationTargetType.HeldMessage, heldId.ToString("D"), "google.comu1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetForwardingSettings_not_found_returns_404()
    {
        var mockRepo = new Mock<ICommitteeRepository>();
        mockRepo.Setup(r => r.GetByIdAsync("nonexistent")).ReturnsAsync((Committee)null);

        var c = CreateController(committeeRepo: mockRepo.Object);
        var result = await c.GetForwardingSettings("nonexistent");

        Assert.IsType<NotFoundResult>(result);
    }

    // ──────────────────────────────────────────────
    // Held message body preview
    // ──────────────────────────────────────────────

    private static HeldMessage SampleHeldMessage(Guid id, string committeeId = "board") =>
        new HeldMessage
        {
            Id = id,
            CommitteeId = committeeId,
            CommitteeEmail = "board@cohad.org",
            InternetMessageId = "<test@example.com>",
            SenderEmail = "sender@example.com",
            SenderName = "Sender",
            Subject = "Test",
            ReceivedUtc = DateTime.UtcNow,
            HeldUtc = DateTime.UtcNow,
            Status = HeldMessageStatus.Held,
            ETag = "etag-1",
        };

    private static IServiceProvider GraphServiceProvider(IGraphMailReader reader)
    {
        var services = new ServiceCollection();
        services.AddSingleton(reader);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task GetHeldMessageBody_returns_html_body()
    {
        var committee = SampleCommittee("board");
        var heldId = Guid.NewGuid();
        var held = SampleHeldMessage(heldId);

        var mockCommitteeRepo = new Mock<ICommitteeRepository>();
        mockCommitteeRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);
        var mockHeldRepo = new Mock<IHeldMessageRepository>();
        mockHeldRepo.Setup(r => r.GetByIdAsync(heldId)).ReturnsAsync(held);

        var graph = new Mock<IGraphMailReader>();
        graph
            .Setup(g => g.GetMessageByInternetIdAsync("board@cohad.org", "<test@example.com>", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Microsoft.Graph.Models.Message
            {
                Body = new Microsoft.Graph.Models.ItemBody
                {
                    ContentType = Microsoft.Graph.Models.BodyType.Html,
                    Content = "<p>Hello spam</p>",
                },
            });

        var c = CreateController(
            committeeRepo: mockCommitteeRepo.Object,
            heldMessageRepo: mockHeldRepo.Object,
            serviceProvider: GraphServiceProvider(graph.Object)
        );
        var result = await c.GetHeldMessageBody("board", heldId);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value, WebJsonOptions);
        Assert.Contains("\"available\":true", json);
        Assert.Contains("\"isHtml\":true", json);
        Assert.Contains("Hello spam", json);
    }

    [Fact]
    public async Task GetHeldMessageBody_returns_text_body()
    {
        var committee = SampleCommittee("board");
        var heldId = Guid.NewGuid();
        var held = SampleHeldMessage(heldId);

        var mockCommitteeRepo = new Mock<ICommitteeRepository>();
        mockCommitteeRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);
        var mockHeldRepo = new Mock<IHeldMessageRepository>();
        mockHeldRepo.Setup(r => r.GetByIdAsync(heldId)).ReturnsAsync(held);

        var graph = new Mock<IGraphMailReader>();
        graph
            .Setup(g => g.GetMessageByInternetIdAsync("board@cohad.org", "<test@example.com>", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Microsoft.Graph.Models.Message
            {
                Body = new Microsoft.Graph.Models.ItemBody
                {
                    ContentType = Microsoft.Graph.Models.BodyType.Text,
                    Content = "plain text body",
                },
            });

        var c = CreateController(
            committeeRepo: mockCommitteeRepo.Object,
            heldMessageRepo: mockHeldRepo.Object,
            serviceProvider: GraphServiceProvider(graph.Object)
        );
        var result = await c.GetHeldMessageBody("board", heldId);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value, WebJsonOptions);
        Assert.Contains("\"available\":true", json);
        Assert.Contains("\"isHtml\":false", json);
        Assert.Contains("plain text body", json);
    }

    [Fact]
    public async Task GetHeldMessageBody_without_graph_returns_unavailable()
    {
        var committee = SampleCommittee("board");
        var heldId = Guid.NewGuid();
        var held = SampleHeldMessage(heldId);

        var mockCommitteeRepo = new Mock<ICommitteeRepository>();
        mockCommitteeRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);
        var mockHeldRepo = new Mock<IHeldMessageRepository>();
        mockHeldRepo.Setup(r => r.GetByIdAsync(heldId)).ReturnsAsync(held);

        // No IGraphMailReader registered
        var services = new ServiceCollection().BuildServiceProvider();
        var c = CreateController(
            committeeRepo: mockCommitteeRepo.Object,
            heldMessageRepo: mockHeldRepo.Object,
            serviceProvider: services
        );
        var result = await c.GetHeldMessageBody("board", heldId);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value, WebJsonOptions);
        Assert.Contains("\"available\":false", json);
    }

    [Fact]
    public async Task GetHeldMessageBody_message_not_in_mailbox_returns_unavailable()
    {
        var committee = SampleCommittee("board");
        var heldId = Guid.NewGuid();
        var held = SampleHeldMessage(heldId);

        var mockCommitteeRepo = new Mock<ICommitteeRepository>();
        mockCommitteeRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);
        var mockHeldRepo = new Mock<IHeldMessageRepository>();
        mockHeldRepo.Setup(r => r.GetByIdAsync(heldId)).ReturnsAsync(held);

        var graph = new Mock<IGraphMailReader>();
        graph
            .Setup(g => g.GetMessageByInternetIdAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Microsoft.Graph.Models.Message)null);

        var c = CreateController(
            committeeRepo: mockCommitteeRepo.Object,
            heldMessageRepo: mockHeldRepo.Object,
            serviceProvider: GraphServiceProvider(graph.Object)
        );
        var result = await c.GetHeldMessageBody("board", heldId);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(ok.Value, WebJsonOptions);
        Assert.Contains("\"available\":false", json);
    }

    [Fact]
    public async Task GetHeldMessageBody_wrong_committee_returns_404()
    {
        var committee = SampleCommittee("board");
        var heldId = Guid.NewGuid();
        var held = SampleHeldMessage(heldId, committeeId: "social"); // belongs to a different committee

        var mockCommitteeRepo = new Mock<ICommitteeRepository>();
        mockCommitteeRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);
        var mockHeldRepo = new Mock<IHeldMessageRepository>();
        mockHeldRepo.Setup(r => r.GetByIdAsync(heldId)).ReturnsAsync(held);

        var c = CreateController(committeeRepo: mockCommitteeRepo.Object, heldMessageRepo: mockHeldRepo.Object);
        var result = await c.GetHeldMessageBody("board", heldId);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetHeldMessageBody_missing_held_returns_404()
    {
        var committee = SampleCommittee("board");
        var heldId = Guid.NewGuid();

        var mockCommitteeRepo = new Mock<ICommitteeRepository>();
        mockCommitteeRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);
        var mockHeldRepo = new Mock<IHeldMessageRepository>();
        mockHeldRepo.Setup(r => r.GetByIdAsync(heldId)).ReturnsAsync((HeldMessage)null);

        var c = CreateController(committeeRepo: mockCommitteeRepo.Object, heldMessageRepo: mockHeldRepo.Object);
        var result = await c.GetHeldMessageBody("board", heldId);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetHeldMessageBody_forbidden_for_wrong_role()
    {
        var social = SampleCommittee("social");
        social.ManagementRole = User.Role.SocialCommittee;
        var heldId = Guid.NewGuid();

        var mockCommitteeRepo = new Mock<ICommitteeRepository>();
        mockCommitteeRepo.Setup(r => r.GetByIdAsync("social")).ReturnsAsync(social);
        var mockHeldRepo = new Mock<IHeldMessageRepository>();

        var c = CreateController(
            committeeRepo: mockCommitteeRepo.Object,
            heldMessageRepo: mockHeldRepo.Object,
            userRepo: RoleUserRepo(User.Role.Board)
        );
        var result = await c.GetHeldMessageBody("social", heldId);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task GetHeldMessageBody_non_held_status_returns_400()
    {
        var committee = SampleCommittee("board");
        var heldId = Guid.NewGuid();
        var held = SampleHeldMessage(heldId);
        held.Status = HeldMessageStatus.Approved; // already actioned

        var mockCommitteeRepo = new Mock<ICommitteeRepository>();
        mockCommitteeRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);
        var mockHeldRepo = new Mock<IHeldMessageRepository>();
        mockHeldRepo.Setup(r => r.GetByIdAsync(heldId)).ReturnsAsync(held);

        // A graph reader is registered to prove the 400 short-circuits before any fetch.
        var graph = new Mock<IGraphMailReader>();
        var c = CreateController(
            committeeRepo: mockCommitteeRepo.Object,
            heldMessageRepo: mockHeldRepo.Object,
            serviceProvider: GraphServiceProvider(graph.Object)
        );
        var result = await c.GetHeldMessageBody("board", heldId);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var json = JsonSerializer.Serialize(bad.Value, WebJsonOptions);
        Assert.Contains("Approved", json);
        graph.Verify(
            g => g.GetMessageByInternetIdAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
    }
}
