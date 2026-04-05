using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
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

public sealed class CommitteeControllerTests
{
    private const string IdentityProviderClaim = "http://schemas.microsoft.com/identity/claims/identityprovider";
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly Guid AliceResidentId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid BobResidentId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid NewPersonResidentId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid SampleHomeId = Guid.Parse("99999999-9999-9999-9999-999999999999");

    private static Resident AliceResident() => new Resident
    {
        Id = AliceResidentId, HomeId = SampleHomeId,
        GivenName = "Alice", Surname = "",
        EmailAddresses = new List<EmailAddress> { new EmailAddress { Address = "alice@example.com" } }
    };

    private static Resident BobResident() => new Resident
    {
        Id = BobResidentId, HomeId = SampleHomeId,
        GivenName = "Bob", Surname = "",
        EmailAddresses = new List<EmailAddress> { new EmailAddress { Address = "bob@example.com" } }
    };

    private static Resident NewPersonResident() => new Resident
    {
        Id = NewPersonResidentId, HomeId = SampleHomeId,
        GivenName = "New", Surname = "Person",
        EmailAddresses = new List<EmailAddress> { new EmailAddress { Address = "new@example.com" } }
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
            .ReturnsAsync((IEnumerable<Guid> ids) =>
                ids.Where(id => allResidents.ContainsKey(id))
                   .Select(id => allResidents[id])
                   .ToList());
        mock.Setup(r => r.GetByIdAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Guid id) => allResidents.GetValueOrDefault(id));
        return mock;
    }

    private static CommitteeController CreateController(
        ICommitteeRepository? committeeRepo = null,
        IResidentRepository? residentRepo = null,
        IDocumentFileStore? fileStore = null,
        IImageUploadHelper? imageUploadHelper = null,
        IGraphMailboxService? graphMailboxService = null,
        IUserRepository? userRepo = null,
        IAuditLogRepository? auditLogRepo = null,
        CommitteeListCache? cache = null,
        string nameId = "u1",
        string idp = "google.com")
    {
        committeeRepo ??= Mock.Of<ICommitteeRepository>();
        residentRepo ??= DefaultResidentRepoMock().Object;
        fileStore ??= Mock.Of<IDocumentFileStore>();
        imageUploadHelper ??= DefaultImageUploadHelper();
        graphMailboxService ??= Mock.Of<IGraphMailboxService>();
        userRepo ??= AdminUserRepo(nameId, idp);
        auditLogRepo ??= Mock.Of<IAuditLogRepository>();
        cache ??= new CommitteeListCache(committeeRepo, new MemoryCache(new MemoryCacheOptions()), WebJsonOptions);

        var c = new CommitteeController(
            committeeRepo,
            residentRepo,
            userRepo,
            auditLogRepo,
            cache,
            fileStore,
            imageUploadHelper,
            graphMailboxService,
            Options.Create(new DocumentStorageOptions()),
            Mock.Of<ILogger<CommitteeController>>());

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

    private static IImageUploadHelper DefaultImageUploadHelper()
    {
        var mock = new Mock<IImageUploadHelper>();
        mock.Setup(h => h.ConvertAndUploadAsync(It.IsAny<IFormFile>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((IFormFile f, string ext, string prefix, string baseName) =>
                new ImageUploadResult($"{prefix}/{baseName}{ext.ToLowerInvariant()}", $"{baseName}{ext.ToLowerInvariant()}",
                    ImageContentTypes.FromExtension(ext), f.Length));
        return mock.Object;
    }

    private static Committee SampleCommittee(string id = "board") => new Committee
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
                DisplayOrder = 1
            },
            new CommitteeMember
            {
                Id = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                ResidentId = BobResidentId,
                Title = "Treasurer",
                Bio = "Manages budget.",
                ReceivesForwardedEmail = true,
                DisplayOrder = 2
            }
        }
    };

    private static IUserRepository AdminUserRepo(string nameId = "u1", string idp = "google.com")
    {
        var uniqueId = idp + nameId;
        var mock = new Mock<IUserRepository>();
        mock.Setup(r => r.GetByUniqueIdAsync(uniqueId))
            .ReturnsAsync(new User
            {
                UniqueId = uniqueId,
                NameIdentifier = nameId,
                IdentityProvider = idp,
                GivenName = "Admin",
                Surname = "User",
                Roles = new List<User.Role> { User.Role.Administrator }
            });
        return mock.Object;
    }

    private static IUserRepository RoleUserRepo(User.Role role, string nameId = "u1", string idp = "google.com")
    {
        var uniqueId = idp + nameId;
        var mock = new Mock<IUserRepository>();
        mock.Setup(r => r.GetByUniqueIdAsync(uniqueId))
            .ReturnsAsync(new User
            {
                UniqueId = uniqueId,
                NameIdentifier = nameId,
                IdentityProvider = idp,
                GivenName = "Role",
                Surname = "User",
                Roles = new List<User.Role> { role }
            });
        return mock.Object;
    }

    private static IFormFile CreateFormFile(string fieldName, string fileName, byte[] content = null)
    {
        content ??= new byte[] { 0x89, 0x50, 0x4E, 0x47 }; // PNG magic bytes
        var ms = new MemoryStream(content);
        return new FormFile(ms, 0, ms.Length, fieldName, fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/jpeg"
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
            new Committee { Id = "social", DisplayName = "Social", DisplayOrder = 3, Members = new() },
            new Committee { Id = "board", DisplayName = "Board", DisplayOrder = 1, Members = new() }
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

    [Fact]
    public async Task GetByKey_returns_NotFound_for_missing_committee()
    {
        var mockRepo = new Mock<ICommitteeRepository>();
        mockRepo.Setup(r => r.GetByIdAsync("missing")).ReturnsAsync((Committee)null);

        var c = CreateController(committeeRepo: mockRepo.Object);
        var result = await c.GetByKey("missing");

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetByKey_returns_committee_card_without_email_or_homeId()
    {
        var committee = SampleCommittee();
        var mockRepo = new Mock<ICommitteeRepository>();
        mockRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);

        var c = CreateController(committeeRepo: mockRepo.Object);
        var result = await c.GetByKey("board");

        var ok = Assert.IsType<OkObjectResult>(result);
        var card = Assert.IsType<CommitteeCard>(ok.Value);
        Assert.Equal("Board", card.DisplayName);
        Assert.Equal(2, card.MemberCount);
        Assert.Equal(2, card.Members.Count);
        // Public view should not expose email or homeId — check that CommitteeMemberCard doesn't have those
        var json = JsonSerializer.Serialize(card.Members[0], WebJsonOptions);
        Assert.DoesNotContain("\"email\"", json);
        Assert.DoesNotContain("\"homeId\"", json);
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

        var payload = JsonSerializer.Serialize(new CommitteeUpdateRequest
        {
            Description = "Updated description",
            DisplayOrder = 1,
            Members = new List<CommitteeMemberUpdate>
            {
                new() { Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), ResidentId = AliceResidentId, DisplayOrder = 1 },
                new() { Id = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), ResidentId = BobResidentId, DisplayOrder = 2 }
            }
        }, WebJsonOptions);

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

        var payload = JsonSerializer.Serialize(new CommitteeUpdateRequest
        {
            Description = committee.Description,
            DisplayOrder = 1,
            Members = new List<CommitteeMemberUpdate>
            {
                new() { Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), ResidentId = AliceResidentId, DisplayOrder = 1 },
                new() { Id = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), ResidentId = BobResidentId, DisplayOrder = 2 }
            }
        }, WebJsonOptions);

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

        var payload = JsonSerializer.Serialize(new CommitteeUpdateRequest
        {
            Description = committee.Description,
            DisplayOrder = 1,
            Members = new List<CommitteeMemberUpdate>
            {
                new() { Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), ResidentId = AliceResidentId, DisplayOrder = 1 },
                new() { Id = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), ResidentId = BobResidentId, DisplayOrder = 2 }
            }
        }, WebJsonOptions);

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

        var payload = JsonSerializer.Serialize(new CommitteeUpdateRequest
        {
            Description = committee.Description,
            DisplayOrder = 1,
            Members = new List<CommitteeMemberUpdate>
            {
                new() { Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), ResidentId = AliceResidentId, DisplayOrder = 1 }
            }
        }, WebJsonOptions);

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

        var payload = JsonSerializer.Serialize(new CommitteeUpdateRequest
        {
            Description = committee.Description,
            DisplayOrder = 1,
            Members = new List<CommitteeMemberUpdate>
            {
                new() { Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), ResidentId = AliceResidentId, DisplayOrder = 1 }
            }
        }, WebJsonOptions);

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

        var payload = JsonSerializer.Serialize(new CommitteeUpdateRequest
        {
            Description = "test",
            Members = new()
        }, WebJsonOptions);

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
        var payload = JsonSerializer.Serialize(new CommitteeUpdateRequest
        {
            Description = "Changed",
            DisplayOrder = 1,
            Members = new List<CommitteeMemberUpdate>
            {
                new() { Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), ResidentId = AliceResidentId, DisplayOrder = 1 }
            }
        }, WebJsonOptions);

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
        var payload = JsonSerializer.Serialize(new CommitteeUpdateRequest
        {
            Description = committee.Description,
            DisplayOrder = 1,
            Members = new List<CommitteeMemberUpdate>
            {
                new() { Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), ResidentId = AliceResidentId, DisplayOrder = 1 }
            }
        }, WebJsonOptions);

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
        mockUpload.Setup(h => h.ConvertAndUploadAsync(It.IsAny<IFormFile>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new ImageUploadResult($"committees/board/{memberId:D}.png", $"{memberId:D}.png", "image/png", 1234));

        var payload = JsonSerializer.Serialize(new CommitteeUpdateRequest
        {
            Description = committee.Description,
            DisplayOrder = 1,
            Members = new List<CommitteeMemberUpdate>
            {
                new() { Id = memberId, ResidentId = AliceResidentId, DisplayOrder = 1 },
                new() { Id = committee.Members[1].Id, ResidentId = BobResidentId, DisplayOrder = 2 }
            }
        }, WebJsonOptions);

        var photo = CreateFormFile("photos", $"photo-{memberId:D}.png");

        var c = CreateController(committeeRepo: mockRepo.Object, fileStore: mockFileStore.Object, imageUploadHelper: mockUpload.Object);
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
        mockRepo.Setup(r => r.UpsertAsync(It.IsAny<Committee>()))
            .Callback<Committee>(c => saved = c)
            .ReturnsAsync((Committee c) => c);

        var payload = JsonSerializer.Serialize(new CommitteeUpdateRequest
        {
            Description = committee.Description,
            DisplayOrder = 1,
            Members = new List<CommitteeMemberUpdate>
            {
                new() { Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), ResidentId = AliceResidentId, DisplayOrder = 1 },
                // New member — Id is empty Guid, should get a generated ID
                new() { Id = Guid.Empty, ResidentId = NewPersonResidentId, DisplayOrder = 3 }
            }
        }, WebJsonOptions);

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
        mockRepo.Verify(r => r.UpsertAsync(It.Is<Committee>(
            comm => comm.Members.Count == 1 && comm.Members[0].ResidentId == BobResidentId)), Times.Once);
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
            PhotoContentType = "image/jpeg"
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
            PhotoBlobPath = "committees/board/aaaaaaaa.jpg"
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
        mockRepo.Setup(r => r.UpsertAsync(It.IsAny<Committee>()))
            .Callback<Committee>(c => saved = c)
            .ReturnsAsync((Committee c) => c);

        var payload = JsonSerializer.Serialize(new CommitteeUpdateRequest
        {
            Description = committee.Description,
            DisplayOrder = 1,
            Members = new List<CommitteeMemberUpdate>
            {
                new() { Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), ResidentId = AliceResidentId, PhotoOffsetY = -10, DisplayOrder = 1 },
                new() { Id = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), ResidentId = BobResidentId, PhotoOffsetY = 200, DisplayOrder = 2 }
            }
        }, WebJsonOptions);

        var c = CreateController(committeeRepo: mockRepo.Object);
        await c.Update("board", payload, new List<IFormFile>());

        Assert.NotNull(saved);
        Assert.Equal(0, saved.Members[0].PhotoOffsetY);    // clamped from -10
        Assert.Equal(100, saved.Members[1].PhotoOffsetY);  // clamped from 200
    }

    [Fact]
    public void PhotoDownloadUrl_is_null_when_no_photo()
    {
        var member = new CommitteeMember
        {
            Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            ResidentId = AliceResidentId,
            PhotoBlobPath = null
        };

        var card = CommitteeMemberCard.FromStorageModel(member, "board", AliceResident());

        Assert.False(card.HasPhoto);
        Assert.Null(card.PhotoDownloadUrl);
    }

    [Fact]
    public async Task GetByKey_returns_photo_urls_that_match_controller_route()
    {
        var committee = SampleCommittee();
        committee.Members[0].PhotoBlobPath = "committees/board/aaaaaaaa.jpg";
        committee.Members[0].PhotoContentType = "image/jpeg";

        var mockRepo = new Mock<ICommitteeRepository>();
        mockRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);

        var c = CreateController(committeeRepo: mockRepo.Object);
        var result = await c.GetByKey("board");

        var ok = Assert.IsType<OkObjectResult>(result);
        var card = Assert.IsType<CommitteeCard>(ok.Value);
        var memberWithPhoto = card.Members.First(m => m.Id == Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        Assert.True(memberWithPhoto.HasPhoto);
        // The URL must be routable to the controller's [HttpGet("{key}/members/{memberId:guid}/photo")] endpoint
        Assert.Equal("/api/committee/board/members/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/photo", memberWithPhoto.PhotoDownloadUrl);
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
        mockFileStore.Setup(s => s.DownloadAsync("committees/board/aaaaaaaa.jpg"))
            .ReturnsAsync(new DocumentFileResult
            {
                Stream = new MemoryStream(Encoding.UTF8.GetBytes("fake-image")),
                ContentType = "image/jpeg",
                EntityTag = new Microsoft.Net.Http.Headers.EntityTagHeaderValue("\"abc123\""),
                LastModified = DateTimeOffset.UtcNow
            });

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
        mockFileStore.Setup(s => s.DownloadAsync(It.IsAny<string>()))
            .ReturnsAsync((DocumentFileResult)null);

        var c = CreateController(committeeRepo: mockRepo.Object, fileStore: mockFileStore.Object);
        var result = await c.DownloadMemberPhoto("board", Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

        Assert.IsType<NotFoundResult>(result);
    }

    // ──────────────────────────────────────────────
    // Forwarding sync
    // ──────────────────────────────────────────────

    [Fact]
    public async Task SyncForwarding_returns_Ok_with_sync_status()
    {
        var committee = SampleCommittee();
        var mockRepo = new Mock<ICommitteeRepository>();
        mockRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);
        mockRepo.Setup(r => r.UpsertAsync(It.IsAny<Committee>())).ReturnsAsync((Committee c) => c);

        var mockGraph = new Mock<IGraphMailboxService>();
        mockGraph.Setup(g => g.SyncForwardingRuleAsync(It.IsAny<Committee>(), It.IsAny<IReadOnlyDictionary<Guid, Resident>>()))
            .ReturnsAsync((Committee c, IReadOnlyDictionary<Guid, Resident> r) =>
            {
                c.LastSyncedUtc = DateTime.UtcNow;
                c.LastSyncStatus = "Success";
                return c;
            });

        var c = CreateController(committeeRepo: mockRepo.Object, graphMailboxService: mockGraph.Object);
        var result = await c.SyncForwarding("board");

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task SyncForwarding_returns_502_and_saves_error_on_failure()
    {
        var committee = SampleCommittee();
        var mockRepo = new Mock<ICommitteeRepository>();
        mockRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);
        mockRepo.Setup(r => r.UpsertAsync(It.IsAny<Committee>())).ReturnsAsync((Committee c) => c);

        var mockGraph = new Mock<IGraphMailboxService>();
        mockGraph.Setup(g => g.SyncForwardingRuleAsync(It.IsAny<Committee>(), It.IsAny<IReadOnlyDictionary<Guid, Resident>>()))
            .ThrowsAsync(new Exception("Graph API timeout"));

        var c = CreateController(committeeRepo: mockRepo.Object, graphMailboxService: mockGraph.Object);
        var result = await c.SyncForwarding("board");

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, status.StatusCode);
        // Verify error was persisted
        mockRepo.Verify(r => r.UpsertAsync(It.Is<Committee>(
            comm => comm.LastSyncStatus == "Failed" && comm.LastSyncError == "Graph API timeout")), Times.Once);
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
        mockUsers.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(new User
        {
            UniqueId = uniqueId,
            GivenName = "Mock",
            Surname = "Admin",
            Roles = new List<User.Role> { User.Role.Administrator }
        });

        var mockAudit = new Mock<IAuditLogRepository>();
        mockAudit.Setup(a => a.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var payload = JsonSerializer.Serialize(new CommitteeUpdateRequest
        {
            Description = "Updated",
            DisplayOrder = 1,
            Members = new List<CommitteeMemberUpdate>
            {
                new() { Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), ResidentId = AliceResidentId, DisplayOrder = 1 }
            }
        }, WebJsonOptions);

        var c = CreateController(committeeRepo: mockRepo.Object, userRepo: mockUsers.Object, auditLogRepo: mockAudit.Object);
        await c.Update("board", payload, new List<IFormFile>());

        mockAudit.Verify(a => a.AddAsync(It.Is<NewAuditLogEntry>(e =>
            e.SubjectId == "board" &&
            e.SubjectName == "Board" &&
            e.Action == "Updated committee." &&
            e.UserId == uniqueId)), Times.Once);
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
        mockUsers.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(new User
        {
            UniqueId = uniqueId,
            GivenName = "Mock",
            Surname = "Admin",
            Roles = new List<User.Role> { User.Role.Administrator }
        });

        var mockAudit = new Mock<IAuditLogRepository>();
        mockAudit.Setup(a => a.AddAsync(It.IsAny<NewAuditLogEntry>())).Returns(Task.CompletedTask);

        var c = CreateController(committeeRepo: mockRepo.Object, userRepo: mockUsers.Object, auditLogRepo: mockAudit.Object);
        await c.RemoveMember("board", Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

        mockAudit.Verify(a => a.AddAsync(It.Is<NewAuditLogEntry>(e =>
            e.SubjectId == "board" &&
            e.Action.Contains("Alice") &&
            e.Action.Contains("Removed member"))), Times.Once);
    }

    [Fact]
    public async Task SyncForwarding_writes_audit_log_on_success()
    {
        var committee = SampleCommittee();
        var uniqueId = UniqueId("u1");

        var mockRepo = new Mock<ICommitteeRepository>();
        mockRepo.Setup(r => r.GetByIdAsync("board")).ReturnsAsync(committee);
        mockRepo.Setup(r => r.UpsertAsync(It.IsAny<Committee>())).ReturnsAsync((Committee c) => c);

        var mockUsers = new Mock<IUserRepository>();
        mockUsers.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(new User
        {
            UniqueId = uniqueId, GivenName = "Mock", Surname = "Admin",
            Roles = new List<User.Role> { User.Role.Administrator }
        });

        var mockGraph = new Mock<IGraphMailboxService>();
        mockGraph.Setup(g => g.SyncForwardingRuleAsync(It.IsAny<Committee>(), It.IsAny<IReadOnlyDictionary<Guid, Resident>>()))
            .ReturnsAsync((Committee c, IReadOnlyDictionary<Guid, Resident> r) =>
            {
                c.LastSyncedUtc = DateTime.UtcNow;
                c.LastSyncStatus = "Success";
                return c;
            });

        var mockAudit = new Mock<IAuditLogRepository>();

        var c = CreateController(committeeRepo: mockRepo.Object, userRepo: mockUsers.Object,
            auditLogRepo: mockAudit.Object, graphMailboxService: mockGraph.Object);
        await c.SyncForwarding("board");

        mockAudit.Verify(a => a.AddAsync(It.Is<NewAuditLogEntry>(e =>
            e.SubjectId == "board" &&
            e.Action.Contains("Synced email forwarding"))), Times.Once);
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

        var payload = JsonSerializer.Serialize(new CommitteeUpdateRequest
        {
            Description = "Updated",
            DisplayOrder = 1,
            Members = new List<CommitteeMemberUpdate>()
        }, WebJsonOptions);

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

        var payload = JsonSerializer.Serialize(new CommitteeUpdateRequest
        {
            Description = "Attempted",
            DisplayOrder = 1,
            Members = new List<CommitteeMemberUpdate>()
        }, WebJsonOptions);

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
}
