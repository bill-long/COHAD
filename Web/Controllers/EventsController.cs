using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using Web.Configuration;
using Web.Models;
using Web.PresentationModels;
using Web.Services;
using Web.Services.Repositories;
using Web.UpdateModels;

namespace Web.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class EventsController : ControllerBase
    {
        private static readonly HashSet<string> AllowedMediaExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png",
            ".jpg",
            ".jpeg",
            ".gif",
            ".webp"
        };

        /// <summary>
        /// Events stay publicly &quot;upcoming&quot; until this long after <see cref="CommunityEvent.StartUtc"/>.
        /// </summary>
        private static readonly TimeSpan UpcomingGraceAfterStart = TimeSpan.FromHours(6);

        /// <summary>Per-household limits for signup requests (avoids overflow in totals and abuse).</summary>
        private const int MaxSignupAdultsPerHousehold = 50;

        private const int MaxSignupChildrenPerHousehold = 50;

        private readonly IUserRepository _userRepository;
        private readonly ICommunityEventRepository _communityEventRepository;
        private readonly IDocumentFileStore _documentFileStore;
        private readonly IAuditLogRepository _auditLogRepository;
        private readonly IOgThumbnailService _ogThumbnailService;
        private readonly IImageUploadHelper _imageUploadHelper;
        private readonly DocumentStorageOptions _storageOptions;

        public EventsController(
            IUserRepository userRepository,
            ICommunityEventRepository communityEventRepository,
            IDocumentFileStore documentFileStore,
            IAuditLogRepository auditLogRepository,
            IOgThumbnailService ogThumbnailService,
            IImageUploadHelper imageUploadHelper,
            IOptions<DocumentStorageOptions> storageOptions)
        {
            _userRepository = userRepository;
            _communityEventRepository = communityEventRepository;
            _documentFileStore = documentFileStore;
            _auditLogRepository = auditLogRepository;
            _ogThumbnailService = ogThumbnailService;
            _imageUploadHelper = imageUploadHelper;
            _storageOptions = storageOptions.Value;
        }

        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> GetUpcoming()
        {
            var now = DateTime.UtcNow;
            var minStartUtc = now - UpcomingGraceAfterStart;
            var payload = (await _communityEventRepository.GetWithStartUtcOnOrAfterAsync(minStartUtc))
                .OrderBy(e => e.StartUtc)
                .Select(CommunityEventCard.FromStorageModel)
                .ToList();

            Response.Headers["Cache-Control"] = "public, max-age=300";
            return Ok(payload);
        }

        [HttpGet("next")]
        [AllowAnonymous]
        public async Task<IActionResult> GetNextUpcoming()
        {
            var now = DateTime.UtcNow;
            var minStartUtc = now - UpcomingGraceAfterStart;
            var next = (await _communityEventRepository.GetWithStartUtcOnOrAfterAsync(minStartUtc))
                .OrderBy(e => e.StartUtc)
                .FirstOrDefault();

            if (next == null)
            {
                return NotFound();
            }

            Response.Headers["Cache-Control"] = "public, max-age=300";
            return Ok(CommunityEventCard.FromStorageModel(next));
        }

        [HttpGet("{segment}")]
        [AllowAnonymous]
        public async Task<IActionResult> GetBySegment(string segment)
        {
            var stored = await _communityEventRepository.GetByRouteSegmentAsync(segment);
            if (stored == null)
            {
                return NotFound();
            }

            var currentUserUniqueId = await TryGetCurrentUserUniqueIdAsync();
            Response.Headers["Cache-Control"] = string.IsNullOrWhiteSpace(currentUserUniqueId)
                ? "public, max-age=300"
                : "private, no-store";
            return Ok(CommunityEventDetail.FromStorageModel(stored, includeSignups: false, currentUserUniqueId));
        }

        [HttpGet("{segment}/promo")]
        [HttpHead("{segment}/promo")]
        [AllowAnonymous]
        public async Task<IActionResult> DownloadPromoMedia(string segment)
        {
            var stored = await _communityEventRepository.GetByRouteSegmentAsync(segment);
            if (stored == null || string.IsNullOrWhiteSpace(stored.PromoMediaBlobPath))
            {
                return NotFound();
            }

            var file = await _documentFileStore.DownloadAsync(stored.PromoMediaBlobPath);
            if (file == null)
            {
                return NotFound();
            }

            var contentType = string.IsNullOrWhiteSpace(file.ContentType)
                ? "application/octet-stream"
                : file.ContentType;
            Response.Headers["Cache-Control"] = "public, no-cache";
            Response.Headers["ETag"] = $"\"{stored.PromoMediaBlobPath.GetHashCode():x}\"";
            // Omit fileDownloadName so the response is served inline for browser display (e.g. img src) rather than as a download attachment.
            return File(file.Stream, contentType);
        }

        [HttpGet("{segment}/promo/og-thumb")]
        [HttpHead("{segment}/promo/og-thumb")]
        [AllowAnonymous]
        public async Task<IActionResult> DownloadPromoThumb(string segment)
        {
            var stored = await _communityEventRepository.GetByRouteSegmentAsync(segment);
            if (stored == null || string.IsNullOrWhiteSpace(stored.PromoMediaBlobPath))
            {
                return NotFound();
            }

            // Serve pre-generated thumbnail if available (either from the document field or the conventional blob path).
            var thumbBlobPath = !string.IsNullOrWhiteSpace(stored.PromoMediaThumbBlobPath)
                ? stored.PromoMediaThumbBlobPath
                : $"events/{stored.Id:D}/og-thumb.jpg";

            var thumbFile = await _documentFileStore.DownloadAsync(thumbBlobPath);
            if (thumbFile != null)
            {
                Response.Headers["Cache-Control"] = "public, max-age=86400";
                return File(thumbFile.Stream, "image/jpeg");
            }

            // Lazy-generate thumbnail for legacy events that predate the thumbnail feature.
            var originalFile = await _documentFileStore.DownloadAsync(stored.PromoMediaBlobPath);
            if (originalFile == null)
            {
                return NotFound();
            }

            byte[] thumbBytes;
            try
            {
                await using (originalFile.Stream)
                {
                    thumbBytes = _ogThumbnailService.GenerateThumbnail(originalFile.Stream);
                }
            }
            catch (InvalidOperationException)
            {
                return StatusCode(StatusCodes.Status415UnsupportedMediaType);
            }

            await using (var thumbStream = new MemoryStream(thumbBytes))
            {
                await _documentFileStore.UploadAsync(thumbBlobPath, thumbStream, "image/jpeg");
            }

            // Best-effort persist the blob path on the event document; ignore concurrency failures.
            try
            {
                var read = await _communityEventRepository.ReadAsync(stored.Id);
                if (read != null && string.IsNullOrWhiteSpace(read.Event.PromoMediaThumbBlobPath))
                {
                    read.Event.PromoMediaThumbBlobPath = thumbBlobPath;
                    await _communityEventRepository.ReplaceAsync(read.Event, read.ETag);
                }
            }
            catch (CosmosException)
            {
                // Non-critical: the blob is already uploaded and will be found via the conventional path on next request.
            }

            Response.Headers["Cache-Control"] = "public, max-age=86400";
            return File(thumbBytes, "image/jpeg");
        }

        [HttpGet("manage")]
        [Authorize]
        public async Task<IActionResult> GetManage()
        {
            var apiUser = await GetApiUserAsync();
            if (apiUser == null)
            {
                return NotFound();
            }

            if (!HasEventManagementAccess(apiUser))
            {
                return Forbid();
            }

            var now = DateTime.UtcNow;
            var all = await _communityEventRepository.GetAllAsync();
            var upcoming = all
                .Where(e => IsInUpcomingWindow(e, now))
                .OrderBy(e => e.StartUtc)
                .Select(e => CommunityEventDetail.FromStorageModel(e, includeSignups: true, null))
                .ToList();
            var past = all
                .Where(e => !IsInUpcomingWindow(e, now))
                .OrderByDescending(e => e.StartUtc)
                .Select(e => CommunityEventDetail.FromStorageModel(e, includeSignups: true, null))
                .ToList();

            return Ok(new ManageEventsPayload { Upcoming = upcoming, Past = past });
        }

        private static bool IsInUpcomingWindow(CommunityEvent e, DateTime utcNow) =>
            e.StartUtc + UpcomingGraceAfterStart >= utcNow;

        [HttpPost("manage")]
        [Authorize]
        public async Task<IActionResult> UpsertManage([FromForm] EventUpsertRequest request)
        {
            var apiUser = await GetApiUserAsync();
            if (apiUser == null)
            {
                return NotFound();
            }

            if (!HasEventManagementAccess(apiUser))
            {
                return Forbid();
            }

            if (request == null)
            {
                return BadRequest("Request body is required.");
            }

            if (string.IsNullOrWhiteSpace(request.Title))
            {
                return BadRequest("Event title is required.");
            }

            if (request.StartUtc == null)
            {
                return BadRequest("Event date/time is required.");
            }

            var now = DateTime.UtcNow;
            var isCreate = request.Id == null;
            CommunityEvent communityEvent;
            CommunityEventReadResult updateRead = null;

            if (isCreate)
            {
                communityEvent = new CommunityEvent
                {
                    Id = Guid.NewGuid(),
                    CreatedByUniqueId = apiUser.UniqueId,
                    CreatedUtc = now,
                    Signups = new List<EventSignup>()
                };
            }
            else
            {
                updateRead = await _communityEventRepository.ReadAsync(request.Id!.Value);
                if (updateRead == null)
                {
                    return NotFound();
                }

                communityEvent = updateRead.Event;
            }

            if (request.PromotionalAsset != null && request.PromotionalAsset.Length > 0)
            {
                if (request.PromotionalAsset.Length > _storageOptions.MaxUploadBytes)
                {
                    return BadRequest($"File size exceeds max allowed size of {_storageOptions.MaxUploadBytes} bytes.");
                }

                var extension = Path.GetExtension(request.PromotionalAsset.FileName);
                if (string.IsNullOrWhiteSpace(extension) || !AllowedMediaExtensions.Contains(extension))
                {
                    return BadRequest("Promotional media must be an image file (PNG, JPEG, GIF, or WebP).");
                }

                var safeBaseName = SanitizeFileName(Path.GetFileNameWithoutExtension(request.PromotionalAsset.FileName));
                if (string.IsNullOrWhiteSpace(safeBaseName))
                {
                    return BadRequest("Uploaded file name is invalid.");
                }

                var uploadResult = await _imageUploadHelper.ConvertAndUploadAsync(
                    request.PromotionalAsset, extension, $"events/{communityEvent.Id:D}", safeBaseName);

                if (!string.IsNullOrWhiteSpace(communityEvent.PromoMediaBlobPath) &&
                    !string.Equals(communityEvent.PromoMediaBlobPath, uploadResult.BlobPath, StringComparison.OrdinalIgnoreCase))
                {
                    await _documentFileStore.DeleteAsync(communityEvent.PromoMediaBlobPath);
                }

                communityEvent.PromoMediaBlobPath = uploadResult.BlobPath;
                communityEvent.PromoMediaDisplayName = uploadResult.FinalDisplayName;
                communityEvent.PromoMediaContentType = uploadResult.ContentType;
                communityEvent.PromoMediaSizeBytes = uploadResult.SizeBytes;

                // Remove any stale thumbnail before generating a new one so a failure
                // doesn't leave an old preview that no longer matches the current promo.
                var thumbBlobPath = $"events/{communityEvent.Id:D}/og-thumb.jpg";
                if (!string.IsNullOrWhiteSpace(communityEvent.PromoMediaThumbBlobPath))
                {
                    await _documentFileStore.DeleteAsync(communityEvent.PromoMediaThumbBlobPath);
                }
                else
                {
                    // Best-effort delete in case a thumb exists at the conventional path from lazy-gen.
                    await _documentFileStore.DeleteAsync(thumbBlobPath);
                }

                communityEvent.PromoMediaThumbBlobPath = null;

                // Generate OG thumbnail for link previews. Non-critical: if this fails the
                // original promo is still usable and the thumbnail will be lazy-generated on first crawler access.
                try
                {
                    // Use the converted JPEG bytes when available to avoid re-decoding the original PNG.
                    Stream thumbSourceStream = uploadResult.ConvertedData != null
                        ? new MemoryStream(uploadResult.ConvertedData)
                        : request.PromotionalAsset.OpenReadStream();
                    await using (thumbSourceStream)
                    {
                        var thumbBytes = _ogThumbnailService.GenerateThumbnail(thumbSourceStream);
                        await using var thumbStream = new MemoryStream(thumbBytes);
                        await _documentFileStore.UploadAsync(thumbBlobPath, thumbStream, "image/jpeg");
                        communityEvent.PromoMediaThumbBlobPath = thumbBlobPath;
                    }
                }
                catch (Exception)
                {
                    // Thumbnail generation failed (e.g. corrupt image); the event save can proceed without it.
                }
            }
            else if (request.RemovePromoMedia && !string.IsNullOrWhiteSpace(communityEvent.PromoMediaBlobPath))
            {
                await _documentFileStore.DeleteAsync(communityEvent.PromoMediaBlobPath);
                if (!string.IsNullOrWhiteSpace(communityEvent.PromoMediaThumbBlobPath))
                {
                    await _documentFileStore.DeleteAsync(communityEvent.PromoMediaThumbBlobPath);
                }
                else
                {
                    await _documentFileStore.DeleteAsync($"events/{communityEvent.Id:D}/og-thumb.jpg");
                }

                communityEvent.PromoMediaBlobPath = null;
                communityEvent.PromoMediaDisplayName = null;
                communityEvent.PromoMediaContentType = null;
                communityEvent.PromoMediaSizeBytes = null;
                communityEvent.PromoMediaThumbBlobPath = null;
            }

            communityEvent.Title = request.Title.Trim();
            communityEvent.Description = (request.Description ?? string.Empty).Trim();
            communityEvent.StartUtc = NormalizeToUtc(request.StartUtc.Value);
            communityEvent.AllowSignups = request.AllowSignups;
            communityEvent.SignupMode = request.SignupMode;
            communityEvent.ModifiedByUniqueId = apiUser.UniqueId;
            communityEvent.ModifiedUtc = now;
            communityEvent.Signups ??= new List<EventSignup>();

            var allEvents = await _communityEventRepository.GetAllAsync();
            var oldSlug = communityEvent.PublicSlug;
            communityEvent.PublicSlug = EventUrlSlug.EnsureUniquePublicSlug(
                communityEvent.Id,
                communityEvent.StartUtc,
                communityEvent.Title,
                allEvents).ToLowerInvariant();

            var normalizedOldSlug = oldSlug?.Trim().ToLowerInvariant();

            if (!isCreate &&
                !string.IsNullOrWhiteSpace(normalizedOldSlug) &&
                !string.Equals(normalizedOldSlug, communityEvent.PublicSlug, StringComparison.OrdinalIgnoreCase))
            {
                communityEvent.PreviousSlugs ??= new List<string>();
                if (!communityEvent.PreviousSlugs.Contains(normalizedOldSlug, StringComparer.OrdinalIgnoreCase))
                {
                    communityEvent.PreviousSlugs.Add(normalizedOldSlug);
                }
            }

            CommunityEvent saved;
            if (isCreate)
            {
                saved = await _communityEventRepository.UpsertAsync(communityEvent);
            }
            else
            {
                try
                {
                    saved = await _communityEventRepository.ReplaceAsync(communityEvent, updateRead!.ETag);
                }
                catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    return NotFound();
                }
                catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
                {
                    // Do not retry: a second read could merge signups while other fields stay stale vs that read.
                    return StatusCode(StatusCodes.Status409Conflict,
                        "Unable to save event due to concurrent updates. Please refresh and try again.");
                }
            }

            await _auditLogRepository.AddAsync(new NewAuditLogEntry
            {
                Id = Guid.NewGuid(),
                SubjectId = saved.Id.ToString("D"),
                SubjectName = saved.Title,
                Action = isCreate ? "Created event." : "Updated event.",
                Time = DateTime.UtcNow,
                UserDisplayName = $"{apiUser.GivenName ?? string.Empty} {apiUser.Surname ?? string.Empty}",
                UserId = apiUser.UniqueId
            });

            return Ok(CommunityEventDetail.FromStorageModel(saved, includeSignups: true, apiUser.UniqueId));
        }

        [HttpDelete("manage/{id:guid}")]
        [Authorize]
        public async Task<IActionResult> DeleteManage(Guid id)
        {
            var apiUser = await GetApiUserAsync();
            if (apiUser == null)
            {
                return NotFound();
            }

            if (!HasEventManagementAccess(apiUser))
            {
                return Forbid();
            }

            var stored = await _communityEventRepository.GetByIdAsync(id);
            if (stored == null)
            {
                return NotFound();
            }

            if (!string.IsNullOrWhiteSpace(stored.PromoMediaBlobPath))
            {
                await _documentFileStore.DeleteAsync(stored.PromoMediaBlobPath);
            }

            if (!string.IsNullOrWhiteSpace(stored.PromoMediaThumbBlobPath))
            {
                await _documentFileStore.DeleteAsync(stored.PromoMediaThumbBlobPath);
            }
            else
            {
                await _documentFileStore.DeleteAsync($"events/{stored.Id:D}/og-thumb.jpg");
            }

            await _communityEventRepository.DeleteAsync(id);
            await _auditLogRepository.AddAsync(new NewAuditLogEntry
            {
                Id = Guid.NewGuid(),
                SubjectId = stored.Id.ToString("D"),
                SubjectName = stored.Title,
                Action = "Deleted event.",
                Time = DateTime.UtcNow,
                UserDisplayName = $"{apiUser.GivenName ?? string.Empty} {apiUser.Surname ?? string.Empty}",
                UserId = apiUser.UniqueId
            });

            return Ok();
        }

        /// <summary>Creates or updates the current user's signup for an event.</summary>
        /// <remarks>
        /// Uses Cosmos optimistic concurrency (If-Match ETag) with bounded retries so concurrent signups do not overwrite each other.
        /// </remarks>
        [HttpPost("{segment}/signup")]
        [Authorize]
        public async Task<IActionResult> SignUp(string segment, [FromBody] EventSignupRequest request)
        {
            var apiUser = await GetApiUserAsync();
            if (apiUser == null)
            {
                return NotFound();
            }

            var routeEvent = await _communityEventRepository.GetByRouteSegmentAsync(segment);
            if (routeEvent == null)
            {
                return NotFound();
            }

            if (!routeEvent.AllowSignups)
            {
                return BadRequest("Signups are not enabled for this event.");
            }

            const int maxAttempts = 10;
            CommunityEvent saved = null;
            var isNewSignup = false;

            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                var read = await _communityEventRepository.ReadAsync(routeEvent.Id);
                if (read == null)
                {
                    return NotFound();
                }

                if (!read.Event.AllowSignups)
                {
                    return BadRequest("Signups are not enabled for this event.");
                }

                var validationError = ValidateSignupRequest(request, read.Event.SignupMode);
                if (validationError != null)
                {
                    return BadRequest(validationError);
                }

                isNewSignup = !(read.Event.Signups?.Any(s => s.UserUniqueId == apiUser.UniqueId) ?? false);
                ApplySignupMutation(read.Event, apiUser, request);

                try
                {
                    saved = await _communityEventRepository.ReplaceAsync(read.Event, read.ETag);
                    break;
                }
                catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    return NotFound();
                }
                catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
                {
                    if (attempt == maxAttempts - 1)
                    {
                        return StatusCode(StatusCodes.Status409Conflict,
                            "Unable to save your signup due to concurrent updates. Please try again.");
                    }
                }
            }

            if (saved == null)
            {
                return StatusCode(StatusCodes.Status409Conflict,
                    "Unable to save your signup due to concurrent updates. Please try again.");
            }

            var countDetail = saved.SignupMode switch
            {
                EventSignupMode.HouseholdOnly => string.Empty,
                EventSignupMode.ChildrenOnly => $" ({request.Children} children)",
                EventSignupMode.AdultsOnly => $" ({request.Adults} adults)",
                _ => $" ({request.Adults} adults, {request.Children} children)"
            };
            var actionPrefix = isNewSignup ? "Signed up for event." : "Updated event signup.";
            await _auditLogRepository.AddAsync(new NewAuditLogEntry
            {
                Id = Guid.NewGuid(),
                SubjectId = saved.Id.ToString("D"),
                SubjectName = saved.Title,
                Action = actionPrefix + countDetail,
                Time = DateTime.UtcNow,
                UserDisplayName = $"{apiUser.GivenName ?? string.Empty} {apiUser.Surname ?? string.Empty}".Trim(),
                UserId = apiUser.UniqueId
            });

            return Ok(CommunityEventDetail.FromStorageModel(saved, includeSignups: false, apiUser.UniqueId));
        }

        private static void ApplySignupMutation(CommunityEvent stored, Models.User apiUser, EventSignupRequest request)
        {
            stored.Signups ??= new List<EventSignup>();
            var existingSignup = stored.Signups.FirstOrDefault(s => s.UserUniqueId == apiUser.UniqueId);
            if (existingSignup == null)
            {
                existingSignup = new EventSignup
                {
                    UserUniqueId = apiUser.UniqueId
                };
                stored.Signups.Add(existingSignup);
            }

            existingSignup.UserDisplayName = $"{apiUser.GivenName ?? string.Empty} {apiUser.Surname ?? string.Empty}".Trim();
            existingSignup.UserEmail = apiUser.Emails;

            switch (stored.SignupMode)
            {
                case EventSignupMode.HouseholdOnly:
                    existingSignup.Adults = 0;
                    existingSignup.Children = 0;
                    existingSignup.AdultNames = new List<string>();
                    existingSignup.ChildNames = new List<string>();
                    break;
                case EventSignupMode.ChildrenOnly:
                    existingSignup.Adults = 0;
                    existingSignup.Children = request.Children;
                    existingSignup.AdultNames = new List<string>();
                    existingSignup.ChildNames = NormalizeNames(request.ChildNames);
                    break;
                case EventSignupMode.AdultsOnly:
                    existingSignup.Adults = request.Adults;
                    existingSignup.Children = 0;
                    existingSignup.AdultNames = NormalizeNames(request.AdultNames);
                    existingSignup.ChildNames = new List<string>();
                    break;
                default:
                    existingSignup.Adults = request.Adults;
                    existingSignup.Children = request.Children;
                    existingSignup.AdultNames = NormalizeNames(request.AdultNames);
                    existingSignup.ChildNames = NormalizeNames(request.ChildNames);
                    break;
            }

            existingSignup.SignedUpUtc = DateTime.UtcNow;

            stored.ModifiedByUniqueId = apiUser.UniqueId;
            stored.ModifiedUtc = DateTime.UtcNow;
        }

        private static string ValidateSignupRequest(EventSignupRequest request, EventSignupMode mode)
        {
            if (request == null)
            {
                return "Please provide a signup request.";
            }

            switch (mode)
            {
                case EventSignupMode.HouseholdOnly:
                    // No count validation needed — just the signup itself.
                    return null;
                case EventSignupMode.ChildrenOnly:
                    if (request.Children < 1)
                    {
                        return "Please provide at least one child.";
                    }

                    if (request.Children > MaxSignupChildrenPerHousehold)
                    {
                        return $"Please enter no more than {MaxSignupChildrenPerHousehold} children per household.";
                    }

                    return null;
                case EventSignupMode.AdultsOnly:
                    if (request.Adults < 1)
                    {
                        return "Please provide at least one adult.";
                    }

                    if (request.Adults > MaxSignupAdultsPerHousehold)
                    {
                        return $"Please enter no more than {MaxSignupAdultsPerHousehold} adults per household.";
                    }

                    return null;
                default:
                    if (request.Adults < 0 || request.Children < 0 || request.Adults + request.Children <= 0)
                    {
                        return "Please provide at least one attendee.";
                    }

                    if (request.Adults > MaxSignupAdultsPerHousehold || request.Children > MaxSignupChildrenPerHousehold)
                    {
                        return $"Please enter no more than {MaxSignupAdultsPerHousehold} adults and {MaxSignupChildrenPerHousehold} children per household.";
                    }

                    return null;
            }
        }

        private async Task<Models.User> GetApiUserAsync()
        {
            var uniqueId = Models.User.GetUniqueIdFromClaims(User.Claims);
            return await _userRepository.GetByUniqueIdAsync(uniqueId);
        }

        private async Task<string> TryGetCurrentUserUniqueIdAsync()
        {
            if (User?.Identity?.IsAuthenticated != true)
            {
                return null;
            }

            try
            {
                var uniqueId = Models.User.GetUniqueIdFromClaims(User.Claims);
                var apiUser = await _userRepository.GetByUniqueIdAsync(uniqueId);
                return apiUser?.UniqueId;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        private static bool HasEventManagementAccess(Models.User user)
        {
            if (user?.Roles == null || user.Roles.Count == 0)
            {
                return false;
            }

            return user.Roles.Contains(Models.User.Role.Resident) &&
                   user.Roles.Any(r => r != Models.User.Role.Resident);
        }

        private static DateTime NormalizeToUtc(DateTime dateTime)
        {
            if (dateTime.Kind == DateTimeKind.Utc)
            {
                return dateTime;
            }

            if (dateTime.Kind == DateTimeKind.Local)
            {
                return dateTime.ToUniversalTime();
            }

            return DateTime.SpecifyKind(dateTime, DateTimeKind.Utc);
        }

        private static List<string> NormalizeNames(IEnumerable<string> names)
        {
            return (names ?? Enumerable.Empty<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Trim())
                .Take(30)
                .ToList();
        }

        private static string SanitizeFileName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
            return cleaned;
        }
    }
}
