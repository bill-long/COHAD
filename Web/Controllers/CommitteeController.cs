using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
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
    public class CommitteeController : ControllerBase
    {
        private static readonly HashSet<string> AllowedPhotoExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".webp"
        };

        private readonly ICommitteeRepository _committeeRepository;
        private readonly IUserRepository _userRepository;
        private readonly IAuditLogRepository _auditLogRepository;
        private readonly CommitteeListCache _listCache;
        private readonly IDocumentFileStore _documentFileStore;
        private readonly IImageUploadHelper _imageUploadHelper;
        private readonly IGraphMailboxService _graphMailboxService;
        private readonly DocumentStorageOptions _storageOptions;
        private readonly ILogger<CommitteeController> _logger;

        public CommitteeController(
            ICommitteeRepository committeeRepository,
            IUserRepository userRepository,
            IAuditLogRepository auditLogRepository,
            CommitteeListCache listCache,
            IDocumentFileStore documentFileStore,
            IImageUploadHelper imageUploadHelper,
            IGraphMailboxService graphMailboxService,
            IOptions<DocumentStorageOptions> storageOptions,
            ILogger<CommitteeController> logger)
        {
            _committeeRepository = committeeRepository;
            _userRepository = userRepository;
            _auditLogRepository = auditLogRepository;
            _listCache = listCache;
            _documentFileStore = documentFileStore;
            _imageUploadHelper = imageUploadHelper;
            _graphMailboxService = graphMailboxService;
            _storageOptions = storageOptions.Value;
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
            var payload = committees
                .OrderBy(c => c.DisplayOrder)
                .Select(CommitteeCard.FromStorageModel)
                .ToList();

            return _listCache.OkWithETag(payload, Request, Response,
                CommitteeListCache.CommitteesResponseKey);
        }

        [HttpGet("{key}")]
        [AllowAnonymous]
        public async Task<IActionResult> GetByKey(string key)
        {
            var committee = await _committeeRepository.GetByIdAsync(key);
            if (committee == null)
                return NotFound();

            return Ok(CommitteeCard.FromStorageModel(committee));
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

        [HttpGet("admin")]
        [Authorize(Policy = "CommitteeEditor")]
        public async Task<IActionResult> GetAllAdmin()
        {
            var apiUser = await GetApiUserAsync();
            if (apiUser == null) return Forbid();

            var committees = await _committeeRepository.GetAllAsync();
            var payload = committees
                .Where(c => CanManageCommittee(apiUser, c))
                .OrderBy(c => c.DisplayOrder)
                .Select(CommitteeAdmin.FromStorageModel)
                .ToList();

            return Ok(payload);
        }

        [HttpGet("admin/{key}")]
        [Authorize(Policy = "CommitteeEditor")]
        public async Task<IActionResult> GetByKeyAdmin(string key)
        {
            var apiUser = await GetApiUserAsync();
            if (apiUser == null) return Forbid();

            var committee = await _committeeRepository.GetByIdAsync(key);
            if (committee == null)
                return NotFound();
            if (!CanManageCommittee(apiUser, committee))
                return Forbid();

            return Ok(CommitteeAdmin.FromStorageModel(committee));
        }

        [HttpPut("admin/{key}")]
        [Authorize(Policy = "CommitteeEditor")]
        public async Task<IActionResult> Update(string key, [FromForm] string payload,
            [FromForm] List<IFormFile> photos)
        {
            var apiUser = await GetApiUserAsync();
            if (apiUser == null) return Forbid();

            var committee = await _committeeRepository.GetByIdAsync(key);
            if (committee == null)
                return NotFound();
            if (!CanManageCommittee(apiUser, committee))
                return Forbid();

            CommitteeUpdateRequest request;
            try
            {
                request = System.Text.Json.JsonSerializer.Deserialize<CommitteeUpdateRequest>(
                    payload, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
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

            var photoLookup = uploadedPhotos
                .ToDictionary(f => Path.GetFileNameWithoutExtension(f.FileName), f => f, StringComparer.OrdinalIgnoreCase);

            if (request.Members != null)
            {
                var expectedPhotoKeys = new HashSet<string>(
                    request.Members
                        .Where(m => m.Id.HasValue && m.Id.Value != Guid.Empty)
                        .Select(m => $"photo-{m.Id.Value:D}"),
                    StringComparer.OrdinalIgnoreCase);

                var unknownPhotoKeys = photoLookup.Keys
                    .Where(k => !expectedPhotoKeys.Contains(k))
                    .ToList();

                if (unknownPhotoKeys.Count > 0)
                {
                    return BadRequest($"Unknown photo upload keys: {string.Join(", ", unknownPhotoKeys)}");
                }
            }

            // Null Members = no member changes (keep existing); prevents accidental data loss from partial payloads
            if (request.Members == null)
            {
                if (uploadedPhotos.Count > 0)
                {
                    return BadRequest("Photo uploads are not allowed when Members is omitted.");
                }

                updatedMembers.AddRange(existingMembers.Values);
            }
            else
            {
                foreach (var mu in request.Members)
                {
                    var memberId = (mu.Id.HasValue && mu.Id.Value != Guid.Empty)
                        ? mu.Id.Value
                        : Guid.NewGuid();

                    existingMembers.TryGetValue(memberId, out var existing);

                    var member = new CommitteeMember
                    {
                        Id = memberId,
                        DisplayName = mu.DisplayName,
                        Title = mu.Title,
                        Bio = mu.Bio,
                        Email = mu.Email,
                        ReceivesForwardedEmail = mu.ReceivesForwardedEmail,
                        PhotoOffsetY = Math.Clamp(mu.PhotoOffsetY, 0, 100),
                        DisplayOrder = mu.DisplayOrder,
                        PhotoBlobPath = existing?.PhotoBlobPath,
                        PhotoContentType = existing?.PhotoContentType
                    };

                    // Handle photo upload for this member
                    if (photoLookup.TryGetValue($"photo-{memberId:D}", out var photoFile))
                    {
                        if (photoFile.Length > _storageOptions.MaxUploadBytes)
                        {
                            return BadRequest($"Photo for member {memberId:D} exceeds max allowed size of {_storageOptions.MaxUploadBytes} bytes.");
                        }

                        if (!string.IsNullOrWhiteSpace(photoFile.ContentType)
                            && !photoFile.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                        {
                            return BadRequest($"Photo for member {memberId:D} must use an image/* Content-Type.");
                        }

                        var ext = Path.GetExtension(photoFile.FileName);
                        if (!AllowedPhotoExtensions.Contains(ext))
                        {
                            return BadRequest($"Unsupported photo format: {ext}");
                        }

                        var result = await _imageUploadHelper.ConvertAndUploadAsync(
                            photoFile, ext, $"committees/{key}", memberId.ToString("D"));

                        // Delete old blob if path changed (e.g. extension change .jpg → .png)
                        if (!string.IsNullOrWhiteSpace(existing?.PhotoBlobPath)
                            && existing.PhotoBlobPath != result.BlobPath)
                        {
                            await _documentFileStore.DeleteAsync(existing.PhotoBlobPath);
                        }

                        member.PhotoBlobPath = result.BlobPath;
                        member.PhotoContentType = result.ContentType;
                    }

                    updatedMembers.Add(member);
                }
            }

            // Delete photos for removed members
            var removedIds = existingMembers.Keys.Except(updatedMembers.Select(m => m.Id)).ToList();
            foreach (var removedId in removedIds)
            {
                if (existingMembers.TryGetValue(removedId, out var removed)
                    && !string.IsNullOrWhiteSpace(removed.PhotoBlobPath))
                {
                    await _documentFileStore.DeleteAsync(removed.PhotoBlobPath);
                }
            }

            committee.Members = updatedMembers;
            await _committeeRepository.UpsertAsync(committee);
            _listCache.Invalidate();

            await AuditAsync(committee.Id, committee.DisplayName, "Updated committee.");

            return Ok(CommitteeAdmin.FromStorageModel(committee));
        }

        [HttpDelete("admin/{key}/members/{memberId:guid}")]
        [Authorize(Policy = "CommitteeEditor")]
        public async Task<IActionResult> RemoveMember(string key, Guid memberId)
        {
            var apiUser = await GetApiUserAsync();
            if (apiUser == null) return Forbid();

            var committee = await _committeeRepository.GetByIdAsync(key);
            if (committee == null)
                return NotFound();
            if (!CanManageCommittee(apiUser, committee))
                return Forbid();

            var member = committee.Members?.FirstOrDefault(m => m.Id == memberId);
            if (member == null)
                return NotFound();

            if (!string.IsNullOrWhiteSpace(member.PhotoBlobPath))
            {
                await _documentFileStore.DeleteAsync(member.PhotoBlobPath);
            }

            committee.Members.Remove(member);
            await _committeeRepository.UpsertAsync(committee);
            _listCache.Invalidate();

            await AuditAsync(committee.Id, committee.DisplayName,
                $"Removed member \"{member.DisplayName}\".");

            return NoContent();
        }

        [HttpPost("admin/{key}/forwarding/sync")]
        [Authorize(Policy = "CommitteeEditor")]
        public async Task<IActionResult> SyncForwarding(string key)
        {
            var apiUser = await GetApiUserAsync();
            if (apiUser == null) return Forbid();

            var committee = await _committeeRepository.GetByIdAsync(key);
            if (committee == null)
                return NotFound();
            if (!CanManageCommittee(apiUser, committee))
                return Forbid();

            try
            {
                committee = await _graphMailboxService.SyncForwardingRuleAsync(committee);
                await _committeeRepository.UpsertAsync(committee);

                var recipientCount = committee.Members?.Count(m => m.ReceivesForwardedEmail && !string.IsNullOrWhiteSpace(m.Email)) ?? 0;

                if (string.Equals(committee.LastSyncStatus, "NotConfigured", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "Forwarding sync unavailable for {Committee}: Graph API is not configured",
                        key);

                    await AuditAsync(committee.Id, committee.DisplayName,
                        "Email forwarding sync skipped — Graph API is not configured.");

                    return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                    {
                        committee.LastSyncedUtc,
                        committee.LastSyncStatus,
                        committee.LastSyncError
                    });
                }

                _logger.LogInformation(
                    "Forwarding sync succeeded for {Committee}: {RecipientCount} recipients",
                    key, recipientCount);

                await AuditAsync(committee.Id, committee.DisplayName,
                    $"Synced email forwarding ({recipientCount} recipients).");

                return Ok(new
                {
                    committee.LastSyncedUtc,
                    committee.LastSyncStatus,
                    committee.LastSyncError
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Forwarding sync failed for {Committee}", key);

                committee.LastSyncedUtc = DateTime.UtcNow;
                committee.LastSyncStatus = "Failed";
                committee.LastSyncError = ex.Message;
                await _committeeRepository.UpsertAsync(committee);

                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    committee.LastSyncedUtc,
                    committee.LastSyncStatus,
                    committee.LastSyncError
                });
            }
        }

        [HttpGet("admin/{key}/forwarding/status")]
        [Authorize(Policy = "CommitteeEditor")]
        public async Task<IActionResult> GetForwardingStatus(string key)
        {
            var apiUser = await GetApiUserAsync();
            if (apiUser == null) return Forbid();

            var committee = await _committeeRepository.GetByIdAsync(key);
            if (committee == null)
                return NotFound();
            if (!CanManageCommittee(apiUser, committee))
                return Forbid();

            return Ok(new
            {
                committee.LastSyncedUtc,
                committee.LastSyncStatus,
                committee.LastSyncError,
                ForwardingRecipients = (committee.Members ?? new List<CommitteeMember>())
                    .Where(m => m.ReceivesForwardedEmail && !string.IsNullOrWhiteSpace(m.Email))
                    .Select(m => new { m.DisplayName, m.Email })
                    .ToList()
            });
        }

        private async Task AuditAsync(string subjectId, string subjectName, string action)
        {
            try
            {
                var uniqueId = Models.User.GetUniqueIdFromClaims(User.Claims);
                var apiUser = await _userRepository.GetByUniqueIdAsync(uniqueId);
                if (apiUser == null) return;

                await _auditLogRepository.AddAsync(new Models.NewAuditLogEntry
                {
                    Id = Guid.NewGuid(),
                    SubjectId = subjectId,
                    SubjectName = subjectName,
                    Action = action,
                    Time = DateTime.UtcNow,
                    UserDisplayName = $"{apiUser.GivenName ?? string.Empty} {apiUser.Surname ?? string.Empty}",
                    UserId = apiUser.UniqueId
                });
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
            if (user.Roles == null) return false;
            if (user.Roles.Contains(Models.User.Role.Administrator)) return true;
            return committee.ManagementRole.HasValue
                && user.Roles.Contains(committee.ManagementRole.Value);
        }
    }
}
