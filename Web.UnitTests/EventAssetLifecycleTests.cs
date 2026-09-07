using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Web.Configuration;
using Web.Controllers;
using Web.Models;
using Web.Services;
using Web.Services.Repositories;
using Web.UpdateModels;
using CosmosException = Microsoft.Azure.Cosmos.CosmosException;

namespace Web.UnitTests;

public sealed class EventAssetLifecycleTests
{
    [Theory]
    [InlineData(false, 404, 0)]
    [InlineData(false, 404, 1003)]
    [InlineData(false, 412, 0)]
    [InlineData(true, 404, 1003)]
    [InlineData(true, 409, 0)]
    [InlineData(true, 400, 0)]
    [InlineData(false, 401, 0)]
    [InlineData(false, 403, 0)]
    [InlineData(false, 413, 0)]
    [InlineData(false, 429, 0)]
    public async Task Rejected_save_preserves_old_assets_and_removes_new_uploads(bool create, int status, int substatus)
    {
        var h = new Harness();
        h.SaveFailure = new CosmosException("Rejected", (HttpStatusCode)status, substatus, "test", 0);
        var request = h.Request(create: create);
        var error = await Record.ExceptionAsync(() => h.Controller().UpsertManage(request));
        if (!create && (status == 412 || substatus == 0 && status == 404))
            Assert.Null(error);
        else
            Assert.Same(h.SaveFailure, error);
        Assert.Equal(new[] { Harness.OldPromo, Harness.OldThumb }, h.Files.Keys.OrderBy(p => p));
        Assert.Equal(Harness.OldPromo, h.Stored.PromoMediaBlobPath);
        Assert.Equal(2, h.Uploaded.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failure_before_database_write_cleans_all_uploads(bool create)
    {
        var h = new Harness();
        h.Events.Setup(r => r.GetAllAsync()).ThrowsAsync(new InvalidOperationException("Slug lookup failed"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Controller().UpsertManage(h.Request(create: create))
        );
        Assert.Equal(2, h.Files.Count);
        Assert.All(h.Uploaded, p => Assert.False(h.Files.ContainsKey(p)));
    }

    [Fact]
    public async Task Partial_converted_upload_is_cleaned_using_its_actual_path()
    {
        var h = new Harness { FailPromoUpload = true };
        await Assert.ThrowsAsync<IOException>(() => h.Controller().UpsertManage(h.Request()));
        Assert.EndsWith("/og-thumb.jpg", Assert.Single(h.Uploaded));
        Assert.Equal(2, h.Files.Count);
        Assert.Equal(Harness.OldPromo, h.Stored.PromoMediaBlobPath);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Unknown_write_outcome_retains_both_old_and_new_assets(bool create, bool committed)
    {
        var h = new Harness { SaveFailure = new TimeoutException(), CommitBeforeFailure = committed };
        await Assert.ThrowsAsync<TimeoutException>(() => h.Controller().UpsertManage(h.Request(create: create)));
        Assert.Equal(4, h.Files.Count);
        Assert.All(h.Uploaded, p => Assert.True(h.Files.ContainsKey(p)));
        if (committed)
        {
            Assert.Contains(h.Stored.PromoMediaBlobPath, h.Uploaded);
            Assert.Contains(h.Stored.PromoMediaThumbBlobPath, h.Uploaded);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replacement_commits_before_deleting_old_assets_and_cleanup_errors_do_not_fail_save(
        bool failCleanup
    )
    {
        var h = new Harness { FailDelete = failCleanup };
        h.BeforeSave = _ =>
        {
            Assert.True(h.Files.ContainsKey(Harness.OldPromo));
            Assert.True(h.Files.ContainsKey(Harness.OldThumb));
            return Task.CompletedTask;
        };
        Assert.IsType<OkObjectResult>(await h.Controller().UpsertManage(h.Request()));
        Assert.NotEqual(h.Stored.PromoMediaBlobPath, h.Stored.PromoMediaThumbBlobPath);
        Assert.True(h.Files.ContainsKey(h.Stored.PromoMediaBlobPath));
        Assert.True(h.Files.ContainsKey(h.Stored.PromoMediaThumbBlobPath));
        Assert.Equal(failCleanup, h.Files.ContainsKey(Harness.OldPromo));
        Assert.Equal(failCleanup, h.Files.ContainsKey(Harness.OldThumb));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Removal_deletes_assets_only_after_success(bool rejected)
    {
        var h = new Harness();
        if (rejected)
            h.SaveFailure = new CosmosException("Conflict", HttpStatusCode.PreconditionFailed, 0, "test", 0);
        var request = h.Request();
        request.PromotionalAsset = null;
        request.RemovePromoMedia = true;
        await h.Controller().UpsertManage(request);
        Assert.Equal(rejected ? 2 : 0, h.Files.Count);
        Assert.Equal(rejected ? Harness.OldPromo : null, h.Stored.PromoMediaBlobPath);
        Assert.Equal(rejected ? Harness.OldThumb : null, h.Stored.PromoMediaThumbBlobPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Thumbnail_failure_does_not_prevent_promo_save_or_leave_partial_thumbnail(bool uploadFailure)
    {
        var h = new Harness { FailThumbUpload = uploadFailure };
        if (!uploadFailure)
            h.Thumbs.Setup(t => t.GenerateThumbnail(It.IsAny<Stream>()))
                .Throws(new InvalidOperationException("Invalid image"));
        Assert.IsType<OkObjectResult>(await h.Controller().UpsertManage(h.Request()));
        Assert.Null(h.Stored.PromoMediaThumbBlobPath);
        Assert.Equal(h.Stored.PromoMediaBlobPath, Assert.Single(h.Files).Key);
    }

    [Fact]
    public async Task Concurrent_same_filename_replacements_cannot_overwrite_or_clean_each_others_assets()
    {
        var h = new Harness();
        var firstWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        h.BeforeSave = async _ =>
        {
            if (++count == 1)
            {
                firstWaiting.SetResult();
                await releaseFirst.Task;
            }
        };
        var first = h.Controller().UpsertManage(h.Request());
        await firstWaiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsType<OkObjectResult>(await h.Controller().UpsertManage(h.Request()));
        var winner = Harness.Clone(h.Stored);
        releaseFirst.SetResult();
        Assert.Equal(409, Assert.IsType<ObjectResult>(await first).StatusCode);
        Assert.Equal(4, h.Uploaded.Distinct().Count());
        Assert.Equal(2, h.Files.Count);
        Assert.True(h.Files.ContainsKey(winner.PromoMediaBlobPath));
        Assert.True(h.Files.ContainsKey(winner.PromoMediaThumbBlobPath));
    }

    [Theory]
    [InlineData("replace")]
    [InlineData("remove")]
    [InlineData("thumbnail")]
    [InlineData("delete")]
    public async Task Lazy_thumbnail_does_not_attach_to_changed_source_or_remove_competing_assets(string change)
    {
        var h = new Harness();
        h.Stored.PromoMediaThumbBlobPath = null;
        h.Thumbs.Setup(t => t.GenerateThumbnail(It.IsAny<Stream>()))
            .Returns(() =>
            {
                switch (change)
                {
                    case "replace":
                        h.Stored.PromoMediaBlobPath = "winner.jpg";
                        h.Files["winner.jpg"] = new byte[] { 9 };
                        break;
                    case "remove":
                        h.Stored.PromoMediaBlobPath = null;
                        break;
                    case "thumbnail":
                        h.Stored.PromoMediaThumbBlobPath = Harness.OldThumb;
                        break;
                    case "delete":
                        h.Events.Setup(r => r.ReadAsync(It.IsAny<Guid>()))
                            .ReturnsAsync((CommunityEventReadResult?)null);
                        break;
                }
                return new byte[] { 3 };
            });
        Assert.IsType<FileContentResult>(await h.Controller().DownloadPromoThumb("event"));
        Assert.False(h.Files.ContainsKey(Assert.Single(h.Uploaded)));
        Assert.True(h.Files.ContainsKey(Harness.OldThumb));
        if (change == "replace")
            Assert.True(h.Files.ContainsKey("winner.jpg"));
        h.Events.Verify(r => r.ReplaceAsync(It.IsAny<CommunityEvent>(), It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(412, false)]
    [InlineData(404, false)]
    [InlineData(503, false)]
    [InlineData(503, true)]
    public async Task Lazy_thumbnail_persistence_uses_unique_path_and_respects_write_outcome(int status, bool committed)
    {
        var h = new Harness { CommitBeforeFailure = committed };
        h.Stored.PromoMediaThumbBlobPath = null;
        if (status != 0)
            h.SaveFailure = new CosmosException("Failure", (HttpStatusCode)status, 0, "test", 0);
        Assert.IsType<FileContentResult>(await h.Controller().DownloadPromoThumb("event"));
        var path = Assert.Single(h.Uploaded);
        Assert.Contains("/thumbs/", path);
        Assert.Equal(status == 0 || status == 503, h.Files.ContainsKey(path));
        Assert.True(h.Files.ContainsKey(Harness.OldThumb));
        if (status == 0 || committed)
            Assert.Equal(path, h.Stored.PromoMediaThumbBlobPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Event_deletion_preserves_assets_until_database_delete_succeeds(bool rejected)
    {
        var h = new Harness();
        h.Events.Setup(r => r.GetByIdAsync(h.Stored.Id)).ReturnsAsync(h.Stored);
        h.Events.Setup(r => r.DeleteAsync(h.Stored.Id))
            .Returns(() =>
            {
                Assert.Equal(2, h.Files.Count);
                return rejected ? Task.FromException(new TimeoutException()) : Task.CompletedTask;
            });
        var error = await Record.ExceptionAsync(() => h.Controller().DeleteManage(h.Stored.Id));
        Assert.Equal(rejected ? 2 : 0, h.Files.Count);
        if (rejected)
            Assert.IsType<TimeoutException>(error);
        else
            Assert.Null(error);
    }

    [Theory]
    [InlineData("replace", false)]
    [InlineData("replace", true)]
    [InlineData("remove", false)]
    [InlineData("remove", true)]
    [InlineData("delete", false)]
    [InlineData("delete", true)]
    [InlineData("lazy", false)]
    [InlineData("lazy", true)]
    public async Task Legacy_conventional_thumbnail_is_cleaned_only_after_confirmed_persistence(
        string operation,
        bool fail
    )
    {
        var h = new Harness();
        var legacyPath = $"events/{h.Stored.Id:D}/og-thumb.jpg";
        h.Files[legacyPath] = new byte[] { 8 };
        h.Stored.PromoMediaThumbBlobPath = null;
        h.Files.Remove(Harness.OldThumb);
        if (fail)
            h.SaveFailure = new CosmosException("Conflict", HttpStatusCode.PreconditionFailed, 0, "test", 0);
        if (operation == "delete")
        {
            h.Events.Setup(r => r.GetByIdAsync(h.Stored.Id)).ReturnsAsync(h.Stored);
            h.Events.Setup(r => r.DeleteAsync(h.Stored.Id))
                .Returns(() =>
                    fail
                        ? Task.FromException(
                            new CosmosException("Conflict", HttpStatusCode.PreconditionFailed, 0, "test", 0)
                        )
                        : Task.CompletedTask
                );
            await Record.ExceptionAsync(() => h.Controller().DeleteManage(h.Stored.Id));
        }
        else if (operation == "lazy")
        {
            await h.Controller().DownloadPromoThumb("event");
        }
        else
        {
            var request = h.Request();
            if (operation == "remove")
            {
                request.PromotionalAsset = null;
                request.RemovePromoMedia = true;
            }
            await h.Controller().UpsertManage(request);
        }
        Assert.Equal(fail, h.Files.ContainsKey(legacyPath));
    }

    [Fact]
    public async Task Legacy_named_promo_is_not_deleted_during_thumbnail_migration_or_metadata_save()
    {
        var h = new Harness();
        var legacyPath = $"events/{h.Stored.Id:D}/og-thumb.jpg";
        h.Files[legacyPath] = new byte[] { 8 };
        h.Stored.PromoMediaBlobPath = legacyPath;
        h.Stored.PromoMediaThumbBlobPath = null;
        await h.Controller().DownloadPromoThumb("event");
        var request = h.Request();
        request.PromotionalAsset = null;
        await h.Controller().UpsertManage(request);
        Assert.True(h.Files.ContainsKey(legacyPath));
        Assert.True(h.Files.ContainsKey(h.Stored.PromoMediaThumbBlobPath));
    }

    private sealed class Harness
    {
        public const string OldPromo = "events/old-promo.jpg";
        public const string OldThumb = "events/old-thumb.jpg";
        public CommunityEvent Stored = new()
        {
            Id = Guid.NewGuid(),
            Title = "Event",
            StartUtc = DateTime.UtcNow,
            PromoMediaBlobPath = OldPromo,
            PromoMediaThumbBlobPath = OldThumb,
        };
        public readonly Dictionary<string, byte[]> Files = new()
        {
            [OldPromo] = new byte[] { 1 },
            [OldThumb] = new byte[] { 2 },
        };
        public readonly List<string> Uploaded = new();
        public readonly Mock<ICommunityEventRepository> Events = new();
        public readonly Mock<IDocumentFileStore> Store = new();
        public readonly Mock<IOgThumbnailService> Thumbs = new();
        public Exception? SaveFailure;
        public bool CommitBeforeFailure,
            FailPromoUpload,
            FailThumbUpload,
            FailDelete;
        public Func<CommunityEvent, Task>? BeforeSave;
        private int version;

        public Harness()
        {
            Events
                .Setup(r => r.ReadAsync(It.IsAny<Guid>()))
                .ReturnsAsync(() =>
                    Stored == null
                        ? null
                        : new CommunityEventReadResult { Event = Clone(Stored), ETag = version.ToString() }
                );
            Events.Setup(r => r.GetByRouteSegmentAsync(It.IsAny<string>())).ReturnsAsync(() => Clone(Stored));
            Events.Setup(r => r.GetAllAsync()).ReturnsAsync(() => new List<CommunityEvent> { Clone(Stored) });
            Events
                .Setup(r => r.ReplaceAsync(It.IsAny<CommunityEvent>(), It.IsAny<string>()))
                .Returns((CommunityEvent e, string etag) => Save(e, etag));
            Events.Setup(r => r.UpsertAsync(It.IsAny<CommunityEvent>())).Returns((CommunityEvent e) => Save(e, null));
            Store
                .Setup(s => s.UploadAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>()))
                .Returns(
                    async (string p, Stream stream, string _) =>
                    {
                        using var bytes = new MemoryStream();
                        await stream.CopyToAsync(bytes);
                        Files[p] = bytes.ToArray();
                        Uploaded.Add(p);
                        if (FailPromoUpload && p.Contains("/promo/") || FailThumbUpload && p.Contains("/thumbs/"))
                            throw new IOException("Partial upload");
                    }
                );
            Store
                .Setup(s => s.DeleteAsync(It.IsAny<string>()))
                .Returns(
                    (string p) =>
                    {
                        if (FailDelete)
                            throw new IOException("Cleanup failed");
                        Files.Remove(p);
                        return Task.CompletedTask;
                    }
                );
            Store
                .Setup(s => s.DownloadAsync(It.IsAny<string>()))
                .ReturnsAsync(
                    (string p) =>
                        Files.TryGetValue(p, out var bytes)
                            ? new DocumentFileResult { Stream = new MemoryStream(bytes), ContentType = "image/jpeg" }
                            : null
                );
            Thumbs.Setup(t => t.GenerateThumbnail(It.IsAny<Stream>())).Returns(new byte[] { 3 });
        }

        private async Task<CommunityEvent> Save(CommunityEvent e, string? etag)
        {
            if (BeforeSave != null)
                await BeforeSave(e);
            if (etag != null && etag != version.ToString())
                throw new CosmosException("Conflict", HttpStatusCode.PreconditionFailed, 0, "test", 0);
            if (SaveFailure == null || CommitBeforeFailure)
            {
                Stored = Clone(e);
                version++;
            }
            if (SaveFailure != null)
                throw SaveFailure;
            return Clone(Stored);
        }

        public static CommunityEvent Clone(CommunityEvent e) =>
            JsonSerializer.Deserialize<CommunityEvent>(JsonSerializer.Serialize(e))
            ?? throw new InvalidOperationException();

        public EventUpsertRequest Request(bool create = false) =>
            new()
            {
                Id = create ? null : Stored.Id,
                Title = "Updated event",
                StartUtc = DateTime.UtcNow.AddDays(1),
                PromotionalAsset = new FormFile(new MemoryStream(new byte[] { 4 }), 0, 1, "asset", "og-thumb.png")
                {
                    Headers = new HeaderDictionary(),
                    ContentType = "image/png",
                },
            };

        public EventsController Controller()
        {
            var user = new Mock<ICurrentUserAccessor>();
            user.Setup(u => u.GetAsync(It.IsAny<ClaimsPrincipal>()))
                .ReturnsAsync(
                    new User
                    {
                        UniqueId = "admin",
                        Roles = new List<User.Role> { User.Role.Resident, User.Role.Administrator },
                    }
                );
            var converter = new Mock<IImageConversionService>();
            converter
                .Setup(c => c.TryConvertToJpeg(It.IsAny<Stream>(), It.IsAny<string>()))
                .Returns(new ImageConversionResult(new byte[] { 5 }, ".jpg", "image/jpeg"));
            return new EventsController(
                user.Object,
                Events.Object,
                Mock.Of<IHomeRepository>(),
                Store.Object,
                Mock.Of<IAuditLogRepository>(),
                Thumbs.Object,
                new ImageUploadHelper(converter.Object, Store.Object),
                Options.Create(new DocumentStorageOptions { MaxUploadBytes = 1024 }),
                Options.Create(new JsonOptions()),
                NullLogger<EventsController>.Instance
            )
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
            };
        }
    }
}
