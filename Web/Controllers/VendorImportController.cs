using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Web.Models;
using Web.Services.Repositories;
using Web.UpdateModels;
using Web.Utils;

namespace Web.Controllers
{
    [Route("api/vendor-import")]
    [ApiController]
    [Authorize(Policy = "Administrator")]
    public class VendorImportController : ControllerBase
    {
        private readonly IVendorRepository _vendorRepository;
        private readonly IVendorReviewRepository _vendorReviewRepository;
        private readonly IYouthServiceListingRepository _youthServiceListingRepository;
        private readonly IUserRepository _userRepository;
        private readonly IAuditLogRepository _auditLogRepository;

        public VendorImportController(
            IVendorRepository vendorRepository,
            IVendorReviewRepository vendorReviewRepository,
            IYouthServiceListingRepository youthServiceListingRepository,
            IUserRepository userRepository,
            IAuditLogRepository auditLogRepository)
        {
            _vendorRepository = vendorRepository;
            _vendorReviewRepository = vendorReviewRepository;
            _youthServiceListingRepository = youthServiceListingRepository;
            _userRepository = userRepository;
            _auditLogRepository = auditLogRepository;
        }

        [HttpPost("reviews")]
        public async Task<IActionResult> ImportReviews([FromBody] List<VendorReviewImportRequest> request)
        {
            var apiUser = await GetApiUserAsync();
            if (apiUser == null)
            {
                return NotFound();
            }

            if (request == null || request.Count == 0)
            {
                return BadRequest("At least one review is required.");
            }

            var imported = 0;
            var skipped = 0;
            foreach (var row in request)
            {
                if (row == null || string.IsNullOrWhiteSpace(row.ReviewText))
                {
                    skipped++;
                    continue;
                }

                var vendor = await _vendorRepository.GetByIdAsync(row.VendorId);
                if (vendor == null)
                {
                    skipped++;
                    continue;
                }

                var displayName = string.IsNullOrWhiteSpace(row.AuthorDisplayName)
                    ? "Community Referrer"
                    : row.AuthorDisplayName.Trim();

                var review = new VendorReview
                {
                    Id = Guid.NewGuid(),
                    VendorId = vendor.Id,
                    AuthorUniqueId = apiUser.UniqueId,
                    AuthorDisplayName = displayName,
                    ReviewText = row.ReviewText.Trim(),
                    CreatedUtc = VendorReviewTimestamps.Unknown,
                    ModifiedUtc = VendorReviewTimestamps.Unknown
                };

                await _vendorReviewRepository.UpsertAsync(review);
                imported++;
            }

            await _auditLogRepository.AddAsync(new NewAuditLogEntry
            {
                Id = Guid.NewGuid(),
                SubjectId = "vendor-reviews-import",
                SubjectName = "Vendor reviews import",
                Action = $"Imported {imported} review(s); skipped {skipped}.",
                Time = DateTime.UtcNow,
                UserDisplayName = $"{apiUser.GivenName ?? string.Empty} {apiUser.Surname ?? string.Empty}".Trim(),
                UserId = apiUser.UniqueId
            });

            return Ok(new
            {
                imported,
                skipped
            });
        }

        [HttpPost("seed")]
        public async Task<IActionResult> ImportVendorsAndReviews([FromBody] VendorSeedImportRequest request)
        {
            var apiUser = await GetApiUserAsync();
            if (apiUser == null)
            {
                return NotFound();
            }

            if (request?.Vendors == null || request.Vendors.Count == 0)
            {
                return BadRequest("At least one vendor is required.");
            }

            var importedVendors = 0;
            var skippedVendors = 0;
            var importedReviews = 0;
            var skippedReviews = 0;
            var importedYouthServices = 0;
            var skippedYouthServices = 0;
            var vendorIdByFingerprint = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            var seenVendorFingerprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in request.Vendors)
            {
                if (row == null || string.IsNullOrWhiteSpace(row.Name) || string.IsNullOrWhiteSpace(row.Fingerprint))
                {
                    skippedVendors++;
                    continue;
                }

                var fingerprint = row.Fingerprint.Trim();
                if (!seenVendorFingerprints.Add(fingerprint))
                {
                    skippedVendors++;
                    continue;
                }

                var vendor = new Vendor
                {
                    Id = Guid.NewGuid(),
                    Name = row.Name.Trim(),
                    Categories = StringListHelper.NormalizeStringList(row.Categories),
                    IsNeighborAffiliated = row.IsNeighborAffiliated,
                    Phone = row.Phone?.Trim(),
                    Email = row.Email?.Trim(),
                    Website = row.Website?.Trim(),
                    Address = row.Address?.Trim(),
                    Notes = row.Notes?.Trim(),
                    CreatedByUniqueId = apiUser.UniqueId,
                    ModifiedByUniqueId = apiUser.UniqueId,
                    CreatedUtc = DateTime.UtcNow,
                    ModifiedUtc = DateTime.UtcNow
                };

                var saved = await _vendorRepository.UpsertAsync(vendor);
                vendorIdByFingerprint[fingerprint] = saved.Id;
                importedVendors++;
            }

            foreach (var row in request.Reviews ?? Enumerable.Empty<VendorSeedImportReview>())
            {
                if (row == null ||
                    string.IsNullOrWhiteSpace(row.VendorFingerprint) ||
                    string.IsNullOrWhiteSpace(row.ReviewText) ||
                    !vendorIdByFingerprint.TryGetValue(row.VendorFingerprint.Trim(), out var vendorId))
                {
                    skippedReviews++;
                    continue;
                }

                var displayName = string.IsNullOrWhiteSpace(row.AuthorDisplayName)
                    ? "Community Referrer"
                    : row.AuthorDisplayName.Trim();

                await _vendorReviewRepository.UpsertAsync(new VendorReview
                {
                    Id = Guid.NewGuid(),
                    VendorId = vendorId,
                    AuthorUniqueId = apiUser.UniqueId,
                    AuthorDisplayName = displayName,
                    ReviewText = row.ReviewText.Trim(),
                    CreatedUtc = VendorReviewTimestamps.Unknown,
                    ModifiedUtc = VendorReviewTimestamps.Unknown
                });
                importedReviews++;
            }

            foreach (var row in request.YouthServices ?? Enumerable.Empty<VendorSeedImportYouthService>())
            {
                if (row == null || string.IsNullOrWhiteSpace(row.Name))
                {
                    skippedYouthServices++;
                    continue;
                }

                var contactMethod = row.ContactMethod == 0
                    ? PreferredContactMethod.Call
                    : PreferredContactMethod.Text;

                await _youthServiceListingRepository.UpsertAsync(new YouthServiceListing
                {
                    Id = Guid.NewGuid(),
                    Name = row.Name.Trim(),
                    Services = StringListHelper.NormalizeStringList(row.Services),
                    BornYear = row.BornYear,
                    Phone = row.Phone?.Trim(),
                    ContactMethod = contactMethod,
                    Email = row.Email?.Trim(),
                    Address = row.Address?.Trim(),
                    ParentNote = row.ParentNote?.Trim(),
                    CreatedByUniqueId = apiUser.UniqueId,
                    ModifiedByUniqueId = apiUser.UniqueId,
                    CreatedUtc = DateTime.UtcNow,
                    ModifiedUtc = DateTime.UtcNow
                });
                importedYouthServices++;
            }

            await _auditLogRepository.AddAsync(new NewAuditLogEntry
            {
                Id = Guid.NewGuid(),
                SubjectId = "vendor-seed-import",
                SubjectName = "Vendor seed import",
                Action = $"Imported vendors={importedVendors}, skipped vendors={skippedVendors}, imported reviews={importedReviews}, skipped reviews={skippedReviews}, imported youth services={importedYouthServices}, skipped youth services={skippedYouthServices}.",
                Time = DateTime.UtcNow,
                UserDisplayName = $"{apiUser.GivenName ?? string.Empty} {apiUser.Surname ?? string.Empty}".Trim(),
                UserId = apiUser.UniqueId
            });

            return Ok(new
            {
                importedVendors,
                skippedVendors,
                importedReviews,
                skippedReviews,
                importedYouthServices,
                skippedYouthServices
            });
        }

        private async Task<User> GetApiUserAsync()
        {
            var uniqueId = Models.User.GetUniqueIdFromClaims(User.Claims);
            return await _userRepository.GetByUniqueIdAsync(uniqueId);
        }

    }
}
