using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Web.Configuration;
using Web.Hubs;
using Web.Models;
using Web.PresentationModels;
using Microsoft.Extensions.DependencyInjection;
using Web.Services;
using Web.Services.Repositories;
using Web.UpdateModels;

namespace Web.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class CommitteeController : ControllerBase
    {
        private static readonly HashSet<string> AllowedPhotoExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png",
            ".jpg",
            ".jpeg",
            ".webp",
        };

        private readonly ICommitteeRepository _committeeRepository;
        private readonly IResidentRepository _residentRepository;
        private readonly IUserRepository _userRepository;
        private readonly IAuditLogRepository _auditLogRepository;
        private readonly CommitteeListCache _listCache;
        private readonly IDocumentFileStore _documentFileStore;
        private readonly IImageUploadHelper _imageUploadHelper;
        private readonly IHeldMessageRepository _heldMessageRepository;
        private readonly IEmailJobRepository _emailJobRepository;
        private readonly EmailJobQueue _emailJobQueue;
        private readonly DocumentStorageOptions _storageOptions;
        private readonly IHubContext<HeldMessageNotificationsHub> _heldMessageHub;
        private readonly ILogger<CommitteeController> _logger;

        public CommitteeController(
            ICommitteeRepository committeeRepository,
            IResidentRepository residentRepository,
            IUserRepository userRepository,
            IAuditLogRepository auditLogRepository,
            CommitteeListCache listCache,
            IDocumentFileStore documentFileStore,
            IImageUploadHelper imageUploadHelper,
            IHeldMessageRepository heldMessageRepository,
            IEmailJobRepository emailJobRepository,
            EmailJobQueue emailJobQueue,
            IOptions<DocumentStorageOptions> storageOptions,
            IHubContext<HeldMessageNotificationsHub> heldMessageHub,
            ILogger<CommitteeController> logger
        )
        {
            _committeeRepository = committeeRepository;
            _residentRepository = residentRepository;
            _userRepository = userRepository;
            _auditLogRepository = auditLogRepository;
            _listCache = listCache;
            _documentFileStore = documentFileStore;
            _imageUploadHelper = imageUploadHelper;
            _heldMessageRepository = heldMessageRepository;
            _emailJobRepository = emailJobRepository;
            _emailJobQueue = emailJobQueue;
            _storageOptions = storageOptions.Value;
            _heldMessageHub = heldMessageHub;
            _logger = logger;
        }

        // ──────────────────────────────────────────────
        // Public endpoints (anonymous)
        // ──────────────────────────────────────────────

        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> GetAll()
        {
            var committees = await _listCache.GetAllAsync();
            var residents = await ResolveResidentsForCommittees(committees);
            var payload = committees
                .OrderBy(c => c.DisplayOrder)
                .Select(c => CommitteeCard.FromStorageModel(c, residents))
                .ToList();

            return _listCache.OkWithETag(payload, Request, Response);
        }

        [HttpGet("{key}/members/{memberId:guid}/photo")]
        [HttpHead("{key}/members/{memberId:guid}/photo")]
        [AllowAnonymous]
        public async Task<IActionResult> DownloadMemberPhoto(string key, Guid memberId)
        {
            var committee = await _committeeRepository.GetByIdAsync(key);
            var member = committee?.Members?.FirstOrDefault(m => m.Id == memberId);
            if (member == null || string.IsNullOrWhiteSpace(member.PhotoBlobPath))
                return NotFound();

            var file = await _documentFileStore.DownloadAsync(member.PhotoBlobPath);
            if (file == null)
                return NotFound();

            var contentType = string.IsNullOrWhiteSpace(file.ContentType)
                ? "application/octet-stream"
                : file.ContentType;
            Response.Headers["Cache-Control"] = "public, no-cache";
            return File(file.Stream, contentType, file.LastModified, file.EntityTag);
        }

        // ──────────────────────────────────────────────
        // Admin endpoints
        // ──────────────────────────────────────────────

        [HttpGet("admin/residents")]
        [Authorize(Policy = "CommitteeEditor")]
        public async Task<IActionResult> GetResidentsForPicker()
        {
            var residents = await _residentRepository.GetAllAsync();
            return Ok(
                residents
                    .OrderBy(r => r.GivenName)
                    .ThenBy(r => r.Surname)
                    .Select(r => new
                    {
                        r.Id,
                        DisplayName = $"{r.GivenName} {r.Surname}".Trim(),
                        Email = r
                            .EmailAddresses?.Select(e => e?.Address)
                            .FirstOrDefault(address => !string.IsNullOrWhiteSpace(address)),
                    })
            );
        }

        [HttpGet("admin")]
        [Authorize(Policy = "CommitteeEditor")]
        public async Task<IActionResult> GetAllAdmin()
        {
            var apiUser = await GetApiUserAsync();
            if (apiUser == null)
                return Forbid();

            var committees = await _committeeRepository.GetAllAsync();
            var manageable = committees.Where(c => CanManageCommittee(apiUser, c)).ToList();
            var residents = await ResolveResidentsForCommittees(manageable);
            var payload = manageable
                .OrderBy(c => c.DisplayOrder)
                .Select(c => CommitteeAdmin.FromStorageModel(c, residents))
                .ToList();

            return Ok(payload);
        }

        [HttpGet("admin/{key}")]
        [Authorize(Policy = "CommitteeEditor")]
        public async Task<IActionResult> GetByKeyAdmin(string key)
        {
            var apiUser = await GetApiUserAsync();
            if (apiUser == null)
                return Forbid();

            var committee = await _committeeRepository.GetByIdAsync(key);
            if (committee == null)
                return NotFound();
            if (!CanManageCommittee(apiUser, committee))
                return Forbid();

            var residents = await ResolveResidentsForCommittees(new[] { committee });
            return Ok(CommitteeAdmin.FromStorageModel(committee, residents));
        }

        [HttpPut("admin/{key}")]
        [Authorize(Policy = "CommitteeEditor")]
        public async Task<IActionResult> Update(
            string key,
            [FromForm] string payload,
            [FromForm] List<IFormFile> photos
        )
        {
            var apiUser = await GetApiUserAsync();
            if (apiUser == null)
                return Forbid();

            var committee = await _committeeRepository.GetByIdAsync(key);
            if (committee == null)
                return NotFound();
            if (!CanManageCommittee(apiUser, committee))
                return Forbid();

            CommitteeUpdateRequest request;
            try
            {
                request = System.Text.Json.JsonSerializer.Deserialize<CommitteeUpdateRequest>(
                    payload,
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
                );
            }
            catch
            {
                return BadRequest("Invalid JSON payload.");
            }

            if (request == null)
                return BadRequest("Empty request.");

            committee.Description = request.Description ?? committee.Description;

            // Only administrators can change committee ordering
            if (request.DisplayOrder.HasValue && apiUser.Roles.Contains(Models.User.Role.Administrator))
            {
                committee.DisplayOrder = request.DisplayOrder.Value;
            }

            // Build a lookup of existing members by ID for merging (defensive: handle bad data)
            var existingMembers = (committee.Members ?? new List<CommitteeMember>())
                .GroupBy(m => m.Id)
                .ToDictionary(g => g.Key, g => g.First());

            // Reject duplicate member IDs in the request
            var requestMemberIds = (request.Members ?? new List<CommitteeMemberUpdate>())
                .Where(m => m.Id.HasValue && m.Id.Value != Guid.Empty)
                .GroupBy(m => m.Id.Value)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key.ToString("D"))
                .ToList();

            if (requestMemberIds.Count > 0)
            {
                return BadRequest($"Duplicate member IDs: {string.Join(", ", requestMemberIds)}");
            }

            var updatedMembers = new List<CommitteeMember>();
            var uploadedPhotos = photos ?? new List<IFormFile>();
            var duplicatePhotoKeys = uploadedPhotos
                .GroupBy(f => Path.GetFileNameWithoutExtension(f.FileName), StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            if (duplicatePhotoKeys.Count > 0)
            {
                return BadRequest($"Duplicate photo upload keys: {string.Join(", ", duplicatePhotoKeys)}");
            }

            var photoLookup = uploadedPhotos.ToDictionary(
                f => Path.GetFileNameWithoutExtension(f.FileName),
                f => f,
                StringComparer.OrdinalIgnoreCase
            );

            if (request.Members != null)
            {
                var expectedPhotoKeys = new HashSet<string>(
                    request
                        .Members.Where(m => m.Id.HasValue && m.Id.Value != Guid.Empty)
                        .Select(m => $"photo-{m.Id.Value:D}"),
                    StringComparer.OrdinalIgnoreCase
                );

                var unknownPhotoKeys = photoLookup.Keys.Where(k => !expectedPhotoKeys.Contains(k)).ToList();

                if (unknownPhotoKeys.Count > 0)
                {
                    return BadRequest($"Unknown photo upload keys: {string.Join(", ", unknownPhotoKeys)}");
                }
            }

            // Track old blobs to delete after the committee is persisted.
            var staleBlobPaths = new List<string>();
            IReadOnlyDictionary<Guid, Resident> resolvedResidents = null;

            if (request.Members == null)
            {
                if (uploadedPhotos.Count > 0)
                {
                    return BadRequest("Photo uploads are not allowed when Members is omitted.");
                }

                // Preserve existing members exactly as-is (no reordering or dedup).
                updatedMembers.AddRange(committee.Members ?? []);
            }
            else
            {
                // Validate all referenced residents exist
                var requestedResidentIds = request
                    .Members.Where(m => m != null)
                    .Select(m => m.ResidentId)
                    .Where(id => id != Guid.Empty)
                    .Distinct()
                    .ToList();

                resolvedResidents =
                    requestedResidentIds.Count > 0
                        ? (await _residentRepository.GetByIdsAsync(requestedResidentIds)).ToDictionary(r => r.Id)
                        : new Dictionary<Guid, Resident>();

                var missingResidentIds = requestedResidentIds
                    .Where(id => !resolvedResidents.ContainsKey(id))
                    .Select(id => id.ToString("D"))
                    .ToList();

                if (missingResidentIds.Count > 0)
                {
                    return BadRequest($"Unknown resident IDs: {string.Join(", ", missingResidentIds)}");
                }

                foreach (var mu in request.Members.Where(m => m != null))
                {
                    if (mu.ResidentId == Guid.Empty)
                    {
                        return BadRequest("Each member must reference a valid ResidentId.");
                    }

                    var memberId = (mu.Id.HasValue && mu.Id.Value != Guid.Empty) ? mu.Id.Value : Guid.NewGuid();

                    existingMembers.TryGetValue(memberId, out var existing);

                    var member = new CommitteeMember
                    {
                        Id = memberId,
                        ResidentId = mu.ResidentId,
                        Title = mu.Title,
                        Bio = mu.Bio,
                        ReceivesForwardedEmail = mu.ReceivesForwardedEmail,
                        PhotoOffsetY = Math.Clamp(mu.PhotoOffsetY, 0, 100),
                        DisplayOrder = mu.DisplayOrder,
                        PhotoBlobPath = existing?.PhotoBlobPath,
                        PhotoContentType = existing?.PhotoContentType,
                    };

                    // Handle photo upload for this member
                    if (photoLookup.TryGetValue($"photo-{memberId:D}", out var photoFile))
                    {
                        if (photoFile.Length > _storageOptions.MaxUploadBytes)
                        {
                            return BadRequest(
                                $"Photo for member {memberId:D} exceeds max allowed size of {_storageOptions.MaxUploadBytes} bytes."
                            );
                        }

                        if (
                            !string.IsNullOrWhiteSpace(photoFile.ContentType)
                            && !photoFile.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                        )
                        {
                            return BadRequest($"Photo for member {memberId:D} must use an image/* Content-Type.");
                        }

                        var ext = Path.GetExtension(photoFile.FileName);
                        if (!AllowedPhotoExtensions.Contains(ext))
                        {
                            return BadRequest($"Unsupported photo format: {ext}");
                        }

                        var result = await _imageUploadHelper.ConvertAndUploadAsync(
                            photoFile,
                            ext,
                            $"committees/{key}",
                            memberId.ToString("D")
                        );

                        // Collect old blob for cleanup after committee is persisted.
                        if (
                            !string.IsNullOrWhiteSpace(existing?.PhotoBlobPath)
                            && existing.PhotoBlobPath != result.BlobPath
                        )
                        {
                            staleBlobPaths.Add(existing.PhotoBlobPath);
                        }

                        member.PhotoBlobPath = result.BlobPath;
                        member.PhotoContentType = result.ContentType;
                    }

                    updatedMembers.Add(member);
                }
            }

            // Collect photos for removed members (deleted after committee is persisted).
            var removedIds = existingMembers.Keys.Except(updatedMembers.Select(m => m.Id)).ToList();
            foreach (var removedId in removedIds)
            {
                if (
                    existingMembers.TryGetValue(removedId, out var removed)
                    && !string.IsNullOrWhiteSpace(removed.PhotoBlobPath)
                )
                {
                    staleBlobPaths.Add(removed.PhotoBlobPath);
                }
            }

            committee.Members = updatedMembers;
            await _committeeRepository.UpsertAsync(committee);
            _listCache.Invalidate();

            // Now safe to delete stale blobs — committee is persisted.
            foreach (var blobPath in staleBlobPaths)
            {
                try
                {
                    await _documentFileStore.DeleteAsync(blobPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete stale photo blob {BlobPath}", blobPath);
                }
            }

            await AuditAsync(committee.Id, committee.DisplayName, "Updated committee.");

            // Reuse already-resolved residents when available; only query again if Members was null (kept existing).
            var residents = resolvedResidents ?? await ResolveResidentsForCommittees(new[] { committee });
            return Ok(CommitteeAdmin.FromStorageModel(committee, residents));
        }

        [HttpDelete("admin/{key}/members/{memberId:guid}")]
        [Authorize(Policy = "CommitteeEditor")]
        public async Task<IActionResult> RemoveMember(string key, Guid memberId)
        {
            var apiUser = await GetApiUserAsync();
            if (apiUser == null)
                return Forbid();

            var committee = await _committeeRepository.GetByIdAsync(key);
            if (committee == null)
                return NotFound();
            if (!CanManageCommittee(apiUser, committee))
                return Forbid();

            var member = committee.Members?.FirstOrDefault(m => m.Id == memberId);
            if (member == null)
                return NotFound();

            var photoBlobPath = member.PhotoBlobPath;

            var resident = await _residentRepository.GetByIdAsync(member.ResidentId);
            var memberName = PresentationModels.CommitteeMemberHelpers.ResidentDisplayName(resident);

            committee.Members.Remove(member);
            await _committeeRepository.UpsertAsync(committee);
            _listCache.Invalidate();

            // Delete photo blob after committee is persisted.
            if (!string.IsNullOrWhiteSpace(photoBlobPath))
            {
                try
                {
                    await _documentFileStore.DeleteAsync(photoBlobPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to delete photo blob {BlobPath} for removed member {MemberId}",
                        photoBlobPath,
                        memberId
                    );
                }
            }

            await AuditAsync(committee.Id, committee.DisplayName, $"Removed member \"{memberName}\".");

            return NoContent();
        }

        [HttpPost("admin/{key}/forwarding/sync")]
        [Authorize(Policy = "CommitteeEditor")]
        [Obsolete("Use the polling mail gateway instead. This endpoint will be removed in a future release.")]
        public Task<IActionResult> SyncForwarding(string key)
        {
            IActionResult result = StatusCode(
                StatusCodes.Status410Gone,
                new
                {
                    message = "The inbox rule sync endpoint has been deprecated. Use the Mail Forwarding settings (enable/disable toggle) in the committee admin panel instead.",
                    alternative = $"PUT /api/committee/admin/{key}/forwarding/settings",
                }
            );
            return Task.FromResult(result);
        }

        [HttpGet("admin/{key}/forwarding/status")]
        [Authorize(Policy = "CommitteeEditor")]
        public async Task<IActionResult> GetForwardingStatus(string key)
        {
            var apiUser = await GetApiUserAsync();
            if (apiUser == null)
                return Forbid();

            var committee = await _committeeRepository.GetByIdAsync(key);
            if (committee == null)
                return NotFound();
            if (!CanManageCommittee(apiUser, committee))
                return Forbid();

            var residents = await ResolveResidentsForCommittees(new[] { committee });

            return Ok(
                new
                {
                    committee.LastSyncedUtc,
                    committee.LastSyncStatus,
                    committee.LastSyncError,
                    ForwardingRecipients = (committee.Members ?? new List<CommitteeMember>())
                        .Where(m =>
                            m.ReceivesForwardedEmail
                            && residents.TryGetValue(m.ResidentId, out var r)
                            && r.EmailAddresses?.Any(e => !string.IsNullOrWhiteSpace(e?.Address)) == true
                        )
                        .Select(m =>
                        {
                            var r = residents.GetValueOrDefault(m.ResidentId);
                            return new
                            {
                                DisplayName = PresentationModels.CommitteeMemberHelpers.ResidentDisplayName(r),
                                Email = r
                                    ?.EmailAddresses?.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e?.Address))
                                    ?.Address,
                            };
                        })
                        .ToList(),
                }
            );
        }

        // ── Forwarding settings (polling mail gateway) ──────────────────

        [HttpGet("admin/{key}/forwarding/settings")]
        [Authorize(Policy = "CommitteeEditor")]
        public async Task<IActionResult> GetForwardingSettings(string key)
        {
            var apiUser = await GetApiUserAsync();
            if (apiUser == null)
                return Forbid();

            var committee = await _committeeRepository.GetByIdAsync(key);
            if (committee == null)
                return NotFound();
            if (!CanManageCommittee(apiUser, committee))
                return Forbid();

            return Ok(
                new
                {
                    committee.ForwardingEnabled,
                    ForwardingSenderFilter = committee.ForwardingSenderFilter.ToString(),
                    committee.LastPollUtc,
                    committee.LastPollStatus,
                    committee.LastPollError,
                }
            );
        }

        [HttpPut("admin/{key}/forwarding/settings")]
        [Authorize(Policy = "CommitteeEditor")]
        public async Task<IActionResult> UpdateForwardingSettings(
            string key,
            [FromBody] ForwardingSettingsUpdate update
        )
        {
            var apiUser = await GetApiUserAsync();
            if (apiUser == null)
                return Forbid();

            var committee = await _committeeRepository.GetByIdAsync(key);
            if (committee == null)
                return NotFound();
            if (!CanManageCommittee(apiUser, committee))
                return Forbid();

            committee.ForwardingEnabled = update.ForwardingEnabled;
            if (committee.ForwardingEnabled)
            {
                // Reject enablement when Graph credentials are not configured — the poller won't run.
                var graphReader = HttpContext.RequestServices.GetService<IGraphMailReader>();
                if (graphReader == null)
                {
                    return BadRequest(new { error = "Cannot enable forwarding: Graph API credentials are not configured." });
                }
            }
            if (!string.IsNullOrWhiteSpace(update.ForwardingSenderFilter))
            {
                if (!Enum.TryParse<ForwardingSenderFilter>(update.ForwardingSenderFilter, true, out var filter))
                {
                    return BadRequest(new { error = $"Invalid ForwardingSenderFilter '{update.ForwardingSenderFilter}'." });
                }
                committee.ForwardingSenderFilter = filter;
            }

            await _committeeRepository.UpsertAsync(committee);
            _listCache.Invalidate();

            var filterLabel = committee.ForwardingSenderFilter.ToString();
            await AuditAsync(
                committee.Id,
                committee.DisplayName,
                committee.ForwardingEnabled
                    ? $"Enabled mail forwarding (sender filter: {filterLabel})."
                    : "Disabled mail forwarding."
            );

            return Ok(
                new
                {
                    committee.ForwardingEnabled,
                    ForwardingSenderFilter = committee.ForwardingSenderFilter.ToString(),
                    committee.LastPollUtc,
                    committee.LastPollStatus,
                    committee.LastPollError,
                }
            );
        }

        // ── Moderation queue (held messages) ────────────────────────────

        [HttpGet("admin/{key}/forwarding/held")]
        [Authorize(Policy = "CommitteeEditor")]
        public async Task<IActionResult> GetHeldMessages(string key)
        {
            var apiUser = await GetApiUserAsync();
            if (apiUser == null)
                return Forbid();

            var committee = await _committeeRepository.GetByIdAsync(key);
            if (committee == null)
                return NotFound();
            if (!CanManageCommittee(apiUser, committee))
                return Forbid();

            var held = await _heldMessageRepository.GetByCommitteeIdAsync(key);
            return Ok(
                held.Select(m => new
                {
                    m.Id,
                    m.SenderEmail,
                    m.SenderName,
                    m.Subject,
                    m.ReceivedUtc,
                    m.HeldUtc,
                    Status = m.Status.ToString(),
                    m.ReviewedByUserId,
                    m.ReviewedUtc,
                })
            );
        }

        [HttpPost("admin/{key}/forwarding/held/{messageId:guid}/approve")]
        [Authorize(Policy = "CommitteeEditor")]
        public async Task<IActionResult> ApproveHeldMessage(string key, Guid messageId)
        {
            var apiUser = await GetApiUserAsync();
            if (apiUser == null)
                return Forbid();

            var committee = await _committeeRepository.GetByIdAsync(key);
            if (committee == null)
                return NotFound();
            if (!CanManageCommittee(apiUser, committee))
                return Forbid();

            var held = await _heldMessageRepository.GetByIdAsync(messageId);
            if (held == null || held.CommitteeId != key)
                return NotFound();
            if (held.Status != HeldMessageStatus.Held)
                return BadRequest(new { error = $"Message is already {held.Status}." });

            // Validate forwarding recipients BEFORE claiming the held message,
            // so a failed validation doesn't leave it permanently marked Approved.
            var residents = await ResolveResidentsForCommittees(new[] { committee });
            var forwardingMembers = (committee.Members ?? new List<CommitteeMember>())
                .Where(m => m.ReceivesForwardedEmail)
                .ToList();

            var recipients = forwardingMembers
                .Select(m => residents.GetValueOrDefault(m.ResidentId))
                .Where(r => r?.EmailAddresses?.Any(e => !string.IsNullOrWhiteSpace(e?.Address)) == true)
                .Select(r => new EmailJobRecipient
                {
                    Email = r.EmailAddresses.First(e => !string.IsNullOrWhiteSpace(e?.Address)).Address,
                    HomeId = r.HomeId,
                    Status = EmailJobRecipientStatus.Pending,
                })
                .GroupBy(r => r.Email, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            if (recipients.Count == 0)
                return BadRequest(new { error = "No forwarding recipients with valid email addresses." });

            // Claim the held message via optimistic concurrency BEFORE creating the job.
            // If two concurrent approvals race, only one will succeed — the loser gets 409.
            held.Status = HeldMessageStatus.Approved;
            held.ReviewedByUserId = apiUser.UniqueId;
            held.ReviewedUtc = DateTime.UtcNow;
            try
            {
                await _heldMessageRepository.UpdateAsync(held);
            }
            catch (InvalidOperationException)
            {
                return Conflict(new { error = "Message was already actioned by another administrator." });
            }

            // Fetch original message body from Graph API (message was moved to Processed folder)
            var graphReader = HttpContext.RequestServices.GetService<IGraphMailReader>();
            string originalBodyHtml = null;
            Microsoft.Graph.Models.Message original = null;
            if (graphReader != null)
            {
                try
                {
                    original = await graphReader.GetMessageByInternetIdAsync(
                        committee.CommitteeEmail,
                        held.InternetMessageId
                    );
                    if (original?.Body != null && !string.IsNullOrEmpty(original.Body.Content))
                    {
                        originalBodyHtml = original.Body.ContentType == Microsoft.Graph.Models.BodyType.Text
                            ? $"<pre style=\"white-space:pre-wrap\">{System.Net.WebUtility.HtmlEncode(original.Body.Content)}</pre>"
                            : original.Body.Content;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Could not retrieve original message body for {InternetMessageId} in {Mailbox}",
                        held.InternetMessageId,
                        committee.CommitteeEmail
                    );
                }
            }

            // Deterministic job ID from held message ID (stable across retries, avoids orphaned blobs)
            var jobId = held.Id;

            // Fetch attachments from the original message (reuse the lookup above)
            var attachments = new List<EmailJobAttachment>();
            if (graphReader != null && original?.HasAttachments == true && original.Id != null)
            {
                try
                {
                    var withAttachments = await graphReader.GetMessageWithAttachmentsAsync(
                        committee.CommitteeEmail, original.Id);

                    if (withAttachments?.Attachments != null)
                    {
                        int attachIndex = 0;
                        foreach (var att in withAttachments.Attachments)
                        {
                            if (att is Microsoft.Graph.Models.FileAttachment fileAtt
                                && fileAtt.IsInline != true
                                && fileAtt.ContentBytes != null
                                && fileAtt.ContentBytes.Length > 0)
                            {
                                var safeName = string.Join("_", (fileAtt.Name ?? $"attachment-{attachIndex}")
                                    .Split(Path.GetInvalidFileNameChars()));
                                if (string.IsNullOrWhiteSpace(safeName)) safeName = "attachment";
                                var blobPath = $"email-jobs/{jobId:D}-attachments/{attachIndex:D4}-{safeName}";

                                try
                                {
                                    using (var stream = new System.IO.MemoryStream(fileAtt.ContentBytes))
                                    {
                                        await _documentFileStore.UploadAsync(blobPath, stream,
                                            fileAtt.ContentType ?? "application/octet-stream");
                                    }
                                }
                                catch (Azure.RequestFailedException blobEx) when (blobEx.Status == 409)
                                {
                                    _logger.LogDebug("Attachment blob {Path} already exists — proceeding", blobPath);
                                }

                                attachments.Add(new EmailJobAttachment
                                {
                                    FileName = safeName,
                                    BlobPath = blobPath,
                                    ContentType = fileAtt.ContentType ?? "application/octet-stream",
                                    Size = fileAtt.ContentBytes.Length,
                                });
                                attachIndex++;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to download attachments for held message {HeldId}", messageId);
                }
            }

            // Build forwarding subject
            var fwdSubject = held.Subject?.StartsWith("Fwd:", StringComparison.OrdinalIgnoreCase) == true
                    || held.Subject?.StartsWith("FW:", StringComparison.OrdinalIgnoreCase) == true
                ? held.Subject
                : $"Fwd: {held.Subject}";

            // Build HTML body with forwarding header + original content
            var htmlBody =
                $"<p><em>This message was approved for forwarding by an administrator.</em></p>"
                + $"<p><strong>From:</strong> {System.Net.WebUtility.HtmlEncode(held.SenderName ?? "")} "
                + $"&lt;{System.Net.WebUtility.HtmlEncode(held.SenderEmail)}&gt;<br/>"
                + $"<strong>Subject:</strong> {System.Net.WebUtility.HtmlEncode(held.Subject ?? "")}</p>"
                + "<hr/>"
                + (originalBodyHtml ?? "<p><em>(Original message body could not be retrieved.)</em></p>");

            var job = new EmailJob
            {
                Id = jobId,
                Status = EmailJobStatus.Queued,
                Category = "committee-forward",
                FromEmail = committee.CommitteeEmail,
                FromDisplay = committee.DisplayName,
                Subject = fwdSubject,
                CreatedUtc = DateTime.UtcNow,
                CreatedByUserId = apiUser.UniqueId,
                CreatedByDisplayName = $"{apiUser.GivenName} {apiUser.Surname}".Trim(),
                MaxRecipientAttempts = 3,
                TotalRecipients = recipients.Count,
                InternetMessageId = held.InternetMessageId,
                ReplyToEmail = string.IsNullOrWhiteSpace(held.SenderEmail) ? null : held.SenderEmail,
                ReplyToDisplay = string.IsNullOrWhiteSpace(held.SenderEmail) ? null : held.SenderName,
                Attachments = attachments,
                Recipients = recipients,
            };

            // Create the forwarding job. If any step fails, revert the held message status.
            try
            {
                job.ContentBlobPath = $"email-jobs/{job.Id:D}.html";
                try
                {
                    using (var stream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(htmlBody)))
                    {
                        await _documentFileStore.UploadAsync(job.ContentBlobPath, stream, "text/html");
                    }
                }
                catch (Azure.RequestFailedException blobEx) when (blobEx.Status == 409)
                {
                    _logger.LogDebug("Body blob {Path} already exists — proceeding", job.ContentBlobPath);
                }

                await _emailJobRepository.AddAsync(job);
                await _emailJobQueue.EnqueueAsync(job.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to create forwarding job for held message {HeldId} — reverting to Held",
                    messageId
                );

                // Revert the held message so it can be retried
                held.Status = HeldMessageStatus.Held;
                held.ReviewedByUserId = null;
                held.ReviewedUtc = null;
                try { await _heldMessageRepository.UpdateAsync(held); }
                catch (Exception revertEx)
                {
                    _logger.LogError(revertEx, "Failed to revert held message {HeldId} status", messageId);
                }

                return StatusCode(StatusCodes.Status500InternalServerError,
                    new { error = "Failed to create forwarding job. The message has been returned to Held status." });
            }

            await AuditAsync(
                committee.Id,
                committee.DisplayName,
                $"Approved held message from {held.SenderEmail} (subject: {held.Subject})."
            );

            _logger.LogInformation(
                "Admin approved held message {HeldId} for committee {Committee} → job {JobId}",
                messageId,
                key,
                job.Id
            );

            await _heldMessageHub.Clients
                .Group(HeldMessageNotificationsHub.AdminGroupName)
                .SendAsync("HeldMessageResolved", new { id = messageId.ToString("D"), committeeId = key });

            return Ok(new { JobId = job.Id, Status = "Approved" });
        }

        [HttpPost("admin/{key}/forwarding/held/{messageId:guid}/reject")]
        [Authorize(Policy = "CommitteeEditor")]
        public async Task<IActionResult> RejectHeldMessage(string key, Guid messageId)
        {
            var apiUser = await GetApiUserAsync();
            if (apiUser == null)
                return Forbid();

            var committee = await _committeeRepository.GetByIdAsync(key);
            if (committee == null)
                return NotFound();
            if (!CanManageCommittee(apiUser, committee))
                return Forbid();

            var held = await _heldMessageRepository.GetByIdAsync(messageId);
            if (held == null || held.CommitteeId != key)
                return NotFound();
            if (held.Status != HeldMessageStatus.Held)
                return BadRequest(new { error = $"Message is already {held.Status}." });

            held.Status = HeldMessageStatus.Rejected;
            held.ReviewedByUserId = apiUser.UniqueId;
            held.ReviewedUtc = DateTime.UtcNow;
            try
            {
                await _heldMessageRepository.UpdateAsync(held);
            }
            catch (InvalidOperationException)
            {
                return Conflict(new { error = "Message was already actioned by another administrator." });
            }

            await AuditAsync(
                committee.Id,
                committee.DisplayName,
                $"Rejected held message from {held.SenderEmail} (subject: {held.Subject})."
            );

            await _heldMessageHub.Clients
                .Group(HeldMessageNotificationsHub.AdminGroupName)
                .SendAsync("HeldMessageResolved", new { id = messageId.ToString("D"), committeeId = key });

            return Ok(new { Status = "Rejected" });
        }

        /// <summary>
        /// Returns all held messages with status Held across all committees.
        /// Used by the notification service to populate the badge on initial load.
        /// </summary>
        [HttpGet("admin/held-messages/pending")]
        [Authorize(Policy = "Administrator")]
        public async Task<IActionResult> GetAllPendingHeldMessages()
        {
            var apiUser = await GetApiUserAsync();
            if (apiUser == null)
                return Forbid();

            var committees = await _committeeRepository.GetAllAsync();
            var committeeMap = committees.ToDictionary(c => c.Id, c => c.DisplayName);

            var allHeld = new List<object>();
            foreach (var c in committees)
            {
                var held = await _heldMessageRepository.GetByCommitteeIdAsync(c.Id);
                allHeld.AddRange(held
                    .Where(h => h.Status == HeldMessageStatus.Held)
                    .Select(h => new
                    {
                        id = h.Id.ToString("D"),
                        committeeId = h.CommitteeId,
                        committeeDisplayName = committeeMap.GetValueOrDefault(h.CommitteeId, h.CommitteeId),
                        senderEmail = h.SenderEmail,
                        senderName = h.SenderName,
                        subject = h.Subject,
                        receivedUtc = h.ReceivedUtc,
                        heldUtc = h.HeldUtc,
                    }));
            }

            return Ok(allHeld);
        }

        // ── Migration (legacy inbox-rule → polling gateway) ─────────────

        /// <summary>
        /// One-time migration: for committees with a legacy inbox rule (<c>GraphMessageRuleId</c>),
        /// deletes the rule from Exchange via Graph API, enables the polling mail gateway with
        /// <c>ForwardingSenderFilter = All</c> (preserving previous behavior), and clears the rule ID.
        /// Requires Administrator policy.
        /// </summary>
        [HttpPost("admin/migrate-forwarding")]
        [Authorize(Policy = "Administrator")]
        public async Task<IActionResult> MigrateForwarding()
        {
            var apiUser = await GetApiUserAsync();
            if (apiUser == null)
                return Forbid();

            var graphReader = HttpContext.RequestServices.GetService<IGraphMailReader>();
            if (graphReader == null)
            {
                return StatusCode(
                    StatusCodes.Status503ServiceUnavailable,
                    new { error = "Graph API credentials are not configured. Cannot delete legacy inbox rules without Graph access." }
                );
            }

            var committees = await _committeeRepository.GetAllAsync();
            var migrated = 0;
            var failed = 0;
            var details = new List<string>();

            foreach (var c in committees.Where(c => !string.IsNullOrEmpty(c.GraphMessageRuleId)))
            {
                try
                {
                    await graphReader.DeleteMessageRuleAsync(c.CommitteeEmail, c.GraphMessageRuleId);
                    _logger.LogInformation(
                        "Deleted legacy inbox rule {RuleId} on {Mailbox}",
                        c.GraphMessageRuleId,
                        c.CommitteeEmail
                    );

                    c.ForwardingEnabled = true;
                    c.ForwardingSenderFilter = ForwardingSenderFilter.All;
                    c.GraphMessageRuleId = null;
                    await _committeeRepository.UpsertAsync(c);

                    migrated++;
                    details.Add($"{c.DisplayName}: migrated");

                    await AuditAsync(c.Id, c.DisplayName, "Migrated from legacy inbox rule to polling mail gateway.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Migration failed for {Mailbox}", c.CommitteeEmail);
                    failed++;
                    details.Add($"{c.DisplayName}: failed — {ex.Message}");
                }
            }

            return Ok(new { migrated, failed, details });
        }

        private async Task AuditAsync(string subjectId, string subjectName, string action)
        {
            try
            {
                var uniqueId = Models.User.GetUniqueIdFromClaims(User.Claims);
                var apiUser = await _userRepository.GetByUniqueIdAsync(uniqueId);
                if (apiUser == null)
                    return;

                await _auditLogRepository.AddAsync(
                    new Models.NewAuditLogEntry
                    {
                        Id = Guid.NewGuid(),
                        SubjectId = subjectId,
                        SubjectName = subjectName,
                        Action = action,
                        Time = DateTime.UtcNow,
                        UserDisplayName = $"{apiUser.GivenName ?? string.Empty} {apiUser.Surname ?? string.Empty}",
                        UserId = apiUser.UniqueId,
                    }
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write audit log for committee {SubjectId}", subjectId);
            }
        }

        private async Task<Models.User> GetApiUserAsync()
        {
            var uniqueId = Models.User.GetUniqueIdFromClaims(User.Claims);
            return await _userRepository.GetByUniqueIdAsync(uniqueId);
        }

        private static bool CanManageCommittee(Models.User user, Committee committee)
        {
            if (user.Roles == null)
                return false;
            if (user.Roles.Contains(Models.User.Role.Administrator))
                return true;
            return committee.ManagementRole.HasValue && user.Roles.Contains(committee.ManagementRole.Value);
        }

        private async Task<IReadOnlyDictionary<Guid, Resident>> ResolveResidentsForCommittees(
            IEnumerable<Committee> committees
        )
        {
            var residentIds = committees
                .SelectMany(c => c.Members ?? new List<CommitteeMember>())
                .Select(m => m.ResidentId)
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();

            if (residentIds.Count == 0)
                return new Dictionary<Guid, Resident>();

            var residents = await _residentRepository.GetByIdsAsync(residentIds);
            return residents.ToDictionary(r => r.Id);
        }
    }
}
