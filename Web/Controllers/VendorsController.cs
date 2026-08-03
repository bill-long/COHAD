using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Web.Models;
using Web.PresentationModels;
using Web.Services;
using Web.Services.Repositories;
using Web.UpdateModels;
using Web.Utils;

namespace Web.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize(Policy = "Resident")]
    public class VendorsController : ControllerBase
    {
        private readonly IVendorRepository _vendorRepository;
        private readonly IVendorReviewRepository _vendorReviewRepository;
        private readonly IVendorFlagRepository _vendorFlagRepository;
        private readonly ICurrentUserAccessor _currentUser;
        private readonly IAuditLogRepository _auditLogRepository;
        private readonly INotificationService _notificationService;
        private readonly ILogger<VendorsController> _logger;

        public VendorsController(
            IVendorRepository vendorRepository,
            IVendorReviewRepository vendorReviewRepository,
            IVendorFlagRepository vendorFlagRepository,
            ICurrentUserAccessor currentUser,
            IAuditLogRepository auditLogRepository,
            INotificationService notificationService,
            ILogger<VendorsController> logger
        )
        {
            _vendorRepository = vendorRepository;
            _vendorReviewRepository = vendorReviewRepository;
            _vendorFlagRepository = vendorFlagRepository;
            _currentUser = currentUser;
            _auditLogRepository = auditLogRepository;
            _notificationService = notificationService;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> GetAll(
            [FromQuery] string q = null,
            [FromQuery] string category = null,
            [FromQuery] bool neighborOnly = false
        )
        {
            var vendors = await _vendorRepository.GetAllAsync();
            var query = vendors.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(q))
            {
                var trimmed = q.Trim();
                query = query.Where(v =>
                    (v.Name?.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (v.Notes?.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (v.Categories?.Any(c => c.Contains(trimmed, StringComparison.OrdinalIgnoreCase)) ?? false)
                );
            }

            if (!string.IsNullOrWhiteSpace(category))
            {
                var trimmedCategory = category.Trim();
                query = query.Where(v =>
                    v.Categories?.Any(c => c.Equals(trimmedCategory, StringComparison.OrdinalIgnoreCase)) == true
                );
            }

            if (neighborOnly)
            {
                query = query.Where(v => v.IsNeighborAffiliated);
            }

            var filtered = query.OrderBy(v => v.Name).ToList();
            var reviewCounts = await _vendorReviewRepository.GetReviewCountsByVendorAsync();
            var latestModifiedByVendor = await _vendorReviewRepository.GetLatestReviewModifiedUtcByVendorAsync();
            var summaries = filtered
                .Select(v =>
                    VendorSummary.FromStorageModel(
                        v,
                        reviewCounts.TryGetValue(v.Id, out var c) ? c : 0,
                        latestModifiedByVendor.TryGetValue(v.Id, out var latest) ? (DateTime?)latest : null
                    )
                )
                .ToList();

            return Ok(summaries);
        }

        [HttpGet("{id:guid}")]
        public async Task<IActionResult> GetById(Guid id)
        {
            var vendor = await _vendorRepository.GetByIdAsync(id);
            if (vendor == null)
            {
                return NotFound();
            }

            var apiUser = await GetApiUserAsync();
            if (apiUser == null)
            {
                return NotFound();
            }

            var isAdmin = apiUser.Roles?.Contains(Models.User.Role.Administrator) == true;
            var reviews = await _vendorReviewRepository.GetByVendorIdAsync(vendor.Id);
            var flags = await _vendorFlagRepository.GetByVendorIdAsync(vendor.Id);
            return Ok(VendorDetail.FromStorageModel(vendor, reviews, flags, apiUser.UniqueId, isAdmin));
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] VendorUpsertRequest request)
        {
            var apiUser = await GetApiUserAsync();
            if (apiUser == null)
            {
                return NotFound();
            }

            if (request == null || string.IsNullOrWhiteSpace(request.Name))
            {
                return BadRequest("Vendor name is required.");
            }

            if (string.IsNullOrWhiteSpace(request.InitialReviewText))
            {
                return BadRequest("An initial review is required when creating a vendor.");
            }

            var now = DateTime.UtcNow;
            var vendor = new Vendor
            {
                Id = Guid.NewGuid(),
                Name = request.Name.Trim(),
                Categories = StringListHelper.NormalizeStringList(request.Categories),
                IsNeighborAffiliated = request.IsNeighborAffiliated,
                Phone = request.Phone?.Trim(),
                Email = request.Email?.Trim(),
                Website = request.Website?.Trim(),
                Address = request.Address?.Trim(),
                Notes = request.Notes?.Trim(),
                CreatedByUniqueId = apiUser.UniqueId,
                ModifiedByUniqueId = apiUser.UniqueId,
                CreatedUtc = now,
                ModifiedUtc = now,
            };

            var saved = await _vendorRepository.UpsertAsync(vendor);
            VendorReview initialReview;
            try
            {
                initialReview = await _vendorReviewRepository.UpsertAsync(
                    new VendorReview
                    {
                        Id = Guid.NewGuid(),
                        VendorId = saved.Id,
                        AuthorUniqueId = apiUser.UniqueId,
                        AuthorDisplayName =
                            $"{apiUser.GivenName ?? string.Empty} {apiUser.Surname ?? string.Empty}".Trim(),
                        ReviewText = request.InitialReviewText.Trim(),
                        CreatedUtc = now,
                        ModifiedUtc = now,
                    }
                );
            }
            catch
            {
                await _vendorRepository.DeleteAsync(saved.Id);
                throw;
            }
            await WriteAudit(apiUser, saved.Id.ToString("D"), saved.Name, "Created vendor.");
            return Ok(
                VendorDetail.FromStorageModel(
                    saved,
                    new List<VendorReview> { initialReview },
                    new List<VendorFlag>(),
                    apiUser.UniqueId,
                    apiUser.Roles?.Contains(Models.User.Role.Administrator) == true
                )
            );
        }

        [HttpPut("{id:guid}")]
        public async Task<IActionResult> Update(Guid id, [FromBody] VendorUpsertRequest request)
        {
            var apiUser = await GetApiUserAsync();
            if (apiUser == null)
            {
                return NotFound();
            }

            var stored = await _vendorRepository.GetByIdAsync(id);
            if (stored == null)
            {
                return NotFound();
            }

            if (!CanEdit(apiUser, stored.CreatedByUniqueId))
            {
                return Forbid();
            }

            if (request == null || string.IsNullOrWhiteSpace(request.Name))
            {
                return BadRequest("Vendor name is required.");
            }

            stored.Name = request.Name.Trim();
            stored.Categories = StringListHelper.NormalizeStringList(request.Categories);
            stored.IsNeighborAffiliated = request.IsNeighborAffiliated;
            stored.Phone = request.Phone?.Trim();
            stored.Email = request.Email?.Trim();
            stored.Website = request.Website?.Trim();
            stored.Address = request.Address?.Trim();
            stored.Notes = request.Notes?.Trim();
            stored.ModifiedByUniqueId = apiUser.UniqueId;
            stored.ModifiedUtc = DateTime.UtcNow;

            var saved = await _vendorRepository.UpsertAsync(stored);
            var reviews = await _vendorReviewRepository.GetByVendorIdAsync(saved.Id);
            var flags = await _vendorFlagRepository.GetByVendorIdAsync(saved.Id);
            await WriteAudit(apiUser, saved.Id.ToString("D"), saved.Name, "Updated vendor.");
            return Ok(
                VendorDetail.FromStorageModel(
                    saved,
                    reviews,
                    flags,
                    apiUser.UniqueId,
                    apiUser.Roles?.Contains(Models.User.Role.Administrator) == true
                )
            );
        }

        [HttpDelete("{id:guid}")]
        public async Task<IActionResult> Delete(Guid id)
        {
            var apiUser = await GetApiUserAsync();
            if (apiUser == null)
            {
                return NotFound();
            }

            var stored = await _vendorRepository.GetByIdAsync(id);
            if (stored == null)
            {
                return NotFound();
            }

            if (!CanEdit(apiUser, stored.CreatedByUniqueId))
            {
                return Forbid();
            }

            var reviews = await _vendorReviewRepository.GetByVendorIdAsync(id);
            if (reviews.Count > 0)
            {
                await Task.WhenAll(reviews.Select(r => _vendorReviewRepository.DeleteByVendorCascadeAsync(id, r.Id)));
            }

            var vendorFlags = await _vendorFlagRepository.GetByVendorIdAsync(id);
            if (vendorFlags.Count > 0)
            {
                await Task.WhenAll(vendorFlags.Select(f => _vendorFlagRepository.DeleteByVendorCascadeAsync(id, f.Id)));
                // The flags are gone; resolve any in-app notifications that pointed at them so they
                // don't linger unresolved (and never escalate to email) for a vendor that no longer
                // exists. Independent resolves — run them in parallel like the cascade deletes above.
                await Task.WhenAll(vendorFlags.Select(f =>
                    _notificationService.ResolveAsync(NotificationTargetType.VendorFlag, f.Id.ToString("D"), apiUser.UniqueId)));
            }

            await _vendorRepository.DeleteAsync(id);

            await WriteAudit(apiUser, id.ToString("D"), stored.Name, "Deleted vendor.");
            return Ok();
        }

        [HttpGet("{id:guid}/reviews")]
        public async Task<IActionResult> GetReviews(Guid id)
        {
            var apiUser = await GetApiUserAsync();
            if (apiUser == null)
            {
                return NotFound();
            }

            var vendor = await _vendorRepository.GetByIdAsync(id);
            if (vendor == null)
            {
                return NotFound();
            }

            var reviews = await _vendorReviewRepository.GetByVendorIdAsync(id);
            var payload = reviews
                .OrderByDescending(r => r.ModifiedUtc)
                .Select(r =>
                    VendorReviewPresentation.FromStorageModel(
                        r,
                        apiUser.Roles?.Contains(Models.User.Role.Administrator) == true
                            || string.Equals(r.AuthorUniqueId, apiUser.UniqueId, StringComparison.OrdinalIgnoreCase)
                    )
                )
                .ToList();
            return Ok(payload);
        }

        [HttpPost("{id:guid}/reviews")]
        public async Task<IActionResult> CreateReview(Guid id, [FromBody] VendorReviewUpsertRequest request)
        {
            var apiUser = await GetApiUserAsync();
            if (apiUser == null)
            {
                return NotFound();
            }

            var vendor = await _vendorRepository.GetByIdAsync(id);
            if (vendor == null)
            {
                return NotFound();
            }

            if (string.IsNullOrWhiteSpace(request?.ReviewText))
            {
                return BadRequest("Review text is required.");
            }

            var existing = await _vendorReviewRepository.GetByVendorAndAuthorAsync(id, apiUser.UniqueId);
            if (existing != null)
            {
                return Conflict("You have already reviewed this vendor. Edit your existing review instead.");
            }

            var now = DateTime.UtcNow;
            var review = new VendorReview
            {
                Id = DeterministicReviewId(id, apiUser.UniqueId),
                VendorId = id,
                AuthorUniqueId = apiUser.UniqueId,
                AuthorDisplayName = $"{apiUser.GivenName ?? string.Empty} {apiUser.Surname ?? string.Empty}".Trim(),
                ReviewText = request.ReviewText?.Trim() ?? string.Empty,
                CreatedUtc = now,
                ModifiedUtc = now,
            };

            var saved = await _vendorReviewRepository.UpsertAsync(review);
            await WriteAudit(apiUser, id.ToString("D"), vendor.Name, "Added vendor review.");
            return Ok(VendorReviewPresentation.FromStorageModel(saved, canEdit: true));
        }

        [HttpPut("{vendorId:guid}/reviews/{reviewId:guid}")]
        public async Task<IActionResult> UpdateReview(
            Guid vendorId,
            Guid reviewId,
            [FromBody] VendorReviewUpsertRequest request
        )
        {
            var apiUser = await GetApiUserAsync();
            if (apiUser == null)
            {
                return NotFound();
            }

            var existing = await _vendorReviewRepository.GetByIdAsync(vendorId, reviewId);
            if (existing == null)
            {
                return NotFound();
            }

            if (!CanEdit(apiUser, existing.AuthorUniqueId))
            {
                return Forbid();
            }

            if (string.IsNullOrWhiteSpace(request?.ReviewText))
            {
                return BadRequest("Review text is required.");
            }

            existing.ReviewText = request.ReviewText?.Trim() ?? string.Empty;
            existing.ModifiedUtc = DateTime.UtcNow;
            var saved = await _vendorReviewRepository.UpsertAsync(existing);

            var isAdminOverride = !string.Equals(
                apiUser.UniqueId,
                existing.AuthorUniqueId,
                StringComparison.OrdinalIgnoreCase
            );
            var auditAction = isAdminOverride
                ? $"Admin edited review by {existing.AuthorDisplayName}."
                : "Updated vendor review.";
            await WriteAudit(apiUser, vendorId.ToString("D"), "Vendor review", auditAction);
            return Ok(VendorReviewPresentation.FromStorageModel(saved, canEdit: true));
        }

        [HttpDelete("{vendorId:guid}/reviews/{reviewId:guid}")]
        public async Task<IActionResult> DeleteReview(Guid vendorId, Guid reviewId)
        {
            var apiUser = await GetApiUserAsync();
            if (apiUser == null)
            {
                return NotFound();
            }

            var existing = await _vendorReviewRepository.GetByIdAsync(vendorId, reviewId);
            if (existing == null)
            {
                return NotFound();
            }

            if (!CanEdit(apiUser, existing.AuthorUniqueId))
            {
                return Forbid();
            }

            await _vendorReviewRepository.DeleteAsync(vendorId, reviewId);

            var isAdminOverride = !string.Equals(
                apiUser.UniqueId,
                existing.AuthorUniqueId,
                StringComparison.OrdinalIgnoreCase
            );
            var auditAction = isAdminOverride
                ? $"Admin deleted review by {existing.AuthorDisplayName}."
                : "Deleted vendor review.";
            await WriteAudit(apiUser, vendorId.ToString("D"), "Vendor review", auditAction);
            return Ok();
        }

        [HttpPost("{id:guid}/flags")]
        public async Task<IActionResult> CreateFlag(Guid id, [FromBody] VendorFlagRequest request)
        {
            var apiUser = await GetApiUserAsync();
            if (apiUser == null)
            {
                return NotFound();
            }

            var vendor = await _vendorRepository.GetByIdAsync(id);
            if (vendor == null)
            {
                return NotFound();
            }

            if (string.IsNullOrWhiteSpace(request?.FlagNote))
            {
                return BadRequest("Flag note is required.");
            }

            var existing = await _vendorFlagRepository.GetPendingByAuthorAsync(id, apiUser.UniqueId);
            if (existing != null)
            {
                // A prior attempt may have saved the flag but failed before raising the notification
                // (e.g. a transient error after Upsert). There is no background sweep that re-raises
                // vendor-flag notifications, so without this an admin would never see the flag. RaiseAsync
                // is idempotent on the target, so re-raise here to heal a missing notification before
                // reporting the duplicate. This heal is best-effort: it must not turn the deterministic
                // 409 into a 500 if the notification store is momentarily unavailable.
                try
                {
                    await RaiseVendorFlagNotificationAsync(existing, vendor);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Failed to re-raise vendor-flag notification while healing a duplicate report for flag {FlagId}", existing.Id);
                }
                return Conflict("You already have a pending report for this vendor.");
            }

            var now = DateTime.UtcNow;
            var flag = new VendorFlag
            {
                Id = Guid.NewGuid(),
                VendorId = id,
                AuthorUniqueId = apiUser.UniqueId,
                AuthorDisplayName = $"{apiUser.GivenName ?? string.Empty} {apiUser.Surname ?? string.Empty}".Trim(),
                FlagNote = request.FlagNote.Trim(),
                Status = "Pending",
                CreatedUtc = now,
                ResolvedUtc = DateTime.MinValue,
            };

            var saved = await _vendorFlagRepository.UpsertAsync(flag);
            await WriteAudit(apiUser, id.ToString("D"), vendor.Name, "Flagged vendor.");
            await RaiseVendorFlagNotificationAsync(saved, vendor);
            return Ok(VendorFlagPresentation.FromStorageModel(saved, includeAuthor: false));
        }

        /// <summary>
        /// Raises the in-app notification for a pending vendor flag. Idempotent on the flag id (via
        /// <see cref="INotificationService.RaiseAsync"/>), so it can safely re-run to heal a notification
        /// a prior attempt failed to create.
        /// </summary>
        private Task RaiseVendorFlagNotificationAsync(VendorFlag flag, Vendor vendor)
        {
            return _notificationService.RaiseAsync(
                NotificationType.VendorFlag,
                NotificationAudience.Administrators,
                NotificationTargetType.VendorFlag,
                flag.Id.ToString("D"),
                "Vendor flagged",
                $"{vendor.Name}: {flag.FlagNote}",
                $"/residents/vendors/{flag.VendorId:D}"
            );
        }

        [HttpDelete("{id:guid}/flags/{flagId:guid}")]
        [Authorize(Policy = "Administrator")]
        public async Task<IActionResult> DismissFlag(Guid id, Guid flagId)
        {
            var apiUser = await GetApiUserAsync();
            if (apiUser == null)
            {
                return NotFound();
            }

            var flag = await _vendorFlagRepository.GetByIdAsync(id, flagId);
            if (flag == null)
            {
                return NotFound();
            }

            var vendor = await _vendorRepository.GetByIdAsync(id);

            flag.Status = "Resolved";
            flag.ResolvedUtc = DateTime.UtcNow;
            await _vendorFlagRepository.UpsertAsync(flag);
            await WriteAudit(apiUser, id.ToString("D"), vendor?.Name ?? id.ToString("D"), "Dismissed vendor flag.");
            await _notificationService.ResolveAsync(NotificationTargetType.VendorFlag, flagId.ToString("D"), apiUser.UniqueId);
            return Ok();
        }

        private static bool CanEdit(User user, string creatorUniqueId)
        {
            if (user == null)
            {
                return false;
            }

            if (string.Equals(user.UniqueId, creatorUniqueId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return user.Roles?.Contains(Models.User.Role.Administrator) == true;
        }

        // Scoped rather than file-wide: enabling nullable across this controller flags unrelated
        // pre-existing code, and the point here is that this helper can return null.
#nullable enable
        /// <summary>The calling user, or null when no user matches the token.</summary>
        private Task<User?> GetApiUserAsync() => _currentUser.GetAsync(User);
#nullable restore

        private async Task WriteAudit(User apiUser, string subjectId, string subjectName, string action)
        {
            await _auditLogRepository.AddAsync(
                new NewAuditLogEntry
                {
                    Id = Guid.NewGuid(),
                    SubjectId = subjectId,
                    SubjectName = subjectName,
                    Action = action,
                    Time = DateTime.UtcNow,
                    UserDisplayName = $"{apiUser.GivenName ?? string.Empty} {apiUser.Surname ?? string.Empty}".Trim(),
                    UserId = apiUser.UniqueId,
                }
            );
        }

        /// <summary>
        /// Produces a deterministic GUID from (vendorId, authorUniqueId) so that two
        /// racing CreateReview requests target the same Cosmos document and the second
        /// upsert is idempotent rather than creating a duplicate.
        /// </summary>
        private static Guid DeterministicReviewId(Guid vendorId, string authorUniqueId)
        {
            var input = Encoding.UTF8.GetBytes($"{vendorId:D}:{authorUniqueId.ToLowerInvariant()}");
            var hash = SHA256.HashData(input);
            return new Guid(hash.AsSpan(0, 16));
        }
    }
}
