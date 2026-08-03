using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
    public class YouthServicesController : ControllerBase
    {
        private readonly IYouthServiceListingRepository _youthServiceListingRepository;
        private readonly IUserRepository _userRepository;
        private readonly ICurrentUserAccessor _currentUser;
        private readonly IAuditLogRepository _auditLogRepository;

        public YouthServicesController(
            IYouthServiceListingRepository youthServiceListingRepository,
            IUserRepository userRepository,
            ICurrentUserAccessor currentUser,
            IAuditLogRepository auditLogRepository
        )
        {
            _youthServiceListingRepository = youthServiceListingRepository;
            _userRepository = userRepository;
            _currentUser = currentUser;
            _auditLogRepository = auditLogRepository;
        }

        [HttpGet]
        public async Task<IActionResult> GetAll([FromQuery] string q = null, [FromQuery] string service = null)
        {
            var apiUser = await GetApiUserAsync();
            if (apiUser == null)
            {
                return NotFound();
            }

            var all = await _youthServiceListingRepository.GetAllAsync();
            var query = all.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(q))
            {
                var trimmed = q.Trim();
                query = query.Where(l =>
                    (l.Name?.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (l.ParentNote?.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ?? false)
                );
            }

            if (!string.IsNullOrWhiteSpace(service))
            {
                var trimmedService = service.Trim();
                query = query.Where(l =>
                    l.Services?.Any(s => s.Equals(trimmedService, StringComparison.OrdinalIgnoreCase)) == true
                );
            }

            var payload = query
                .OrderBy(l => ExtractSurname(l.Name), StringComparer.OrdinalIgnoreCase)
                .ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
                .Select(l => YouthServiceListingPresentation.FromStorageModel(l, CanEdit(apiUser, l.CreatedByUniqueId)))
                .ToList();
            return Ok(payload);
        }

        [HttpGet("{id:guid}")]
        public async Task<IActionResult> GetById(Guid id)
        {
            var apiUser = await GetApiUserAsync();
            if (apiUser == null)
            {
                return NotFound();
            }

            var listing = await _youthServiceListingRepository.GetByIdAsync(id);
            if (listing == null)
            {
                return NotFound();
            }

            return Ok(
                YouthServiceListingPresentation.FromStorageModel(listing, CanEdit(apiUser, listing.CreatedByUniqueId))
            );
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] YouthServiceUpsertRequest request)
        {
            var apiUser = await GetApiUserAsync();
            if (apiUser == null)
            {
                return NotFound();
            }

            if (request == null || string.IsNullOrWhiteSpace(request.Name))
            {
                return BadRequest("Name is required.");
            }

            if (request.BornYear != null && (request.BornYear < 1900 || request.BornYear > DateTime.UtcNow.Year))
            {
                return BadRequest("Born year is invalid.");
            }

            var now = DateTime.UtcNow;
            var listing = new YouthServiceListing
            {
                Id = Guid.NewGuid(),
                Name = request.Name.Trim(),
                Services = StringListHelper.NormalizeStringList(request.Services),
                BornYear = request.BornYear,
                Phone = request.Phone?.Trim(),
                ContactMethod = request.ContactMethod,
                Email = request.Email?.Trim(),
                Address = request.Address?.Trim(),
                ParentNote = request.ParentNote?.Trim(),
                CreatedByUniqueId = apiUser.UniqueId,
                ModifiedByUniqueId = apiUser.UniqueId,
                CreatedUtc = now,
                ModifiedUtc = now,
            };

            var saved = await _youthServiceListingRepository.UpsertAsync(listing);
            await WriteAudit(apiUser, saved.Id.ToString("D"), saved.Name, "Created youth service listing.");
            return Ok(
                YouthServiceListingPresentation.FromStorageModel(saved, CanEdit(apiUser, saved.CreatedByUniqueId))
            );
        }

        [HttpPut("{id:guid}")]
        public async Task<IActionResult> Update(Guid id, [FromBody] YouthServiceUpsertRequest request)
        {
            var apiUser = await GetApiUserAsync();
            if (apiUser == null)
            {
                return NotFound();
            }

            var stored = await _youthServiceListingRepository.GetByIdAsync(id);
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
                return BadRequest("Name is required.");
            }

            if (request.BornYear != null && (request.BornYear < 1900 || request.BornYear > DateTime.UtcNow.Year))
            {
                return BadRequest("Born year is invalid.");
            }

            stored.Name = request.Name.Trim();
            stored.Services = StringListHelper.NormalizeStringList(request.Services);
            stored.BornYear = request.BornYear;
            stored.Phone = request.Phone?.Trim();
            stored.ContactMethod = request.ContactMethod;
            stored.Email = request.Email?.Trim();
            stored.Address = request.Address?.Trim();
            stored.ParentNote = request.ParentNote?.Trim();
            stored.ModifiedByUniqueId = apiUser.UniqueId;
            stored.ModifiedUtc = DateTime.UtcNow;

            var saved = await _youthServiceListingRepository.UpsertAsync(stored);
            await WriteAudit(apiUser, saved.Id.ToString("D"), saved.Name, "Updated youth service listing.");
            return Ok(
                YouthServiceListingPresentation.FromStorageModel(saved, CanEdit(apiUser, saved.CreatedByUniqueId))
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

            var stored = await _youthServiceListingRepository.GetByIdAsync(id);
            if (stored == null)
            {
                return NotFound();
            }

            if (!CanEdit(apiUser, stored.CreatedByUniqueId))
            {
                return Forbid();
            }

            await _youthServiceListingRepository.DeleteAsync(id);
            await WriteAudit(apiUser, id.ToString("D"), stored.Name, "Deleted youth service listing.");
            return Ok();
        }

        [HttpPut("{id:guid}/owner")]
        [Authorize(Policy = "Administrator")]
        public async Task<IActionResult> ReassignOwner(Guid id, [FromBody] YouthServiceOwnerUpdateRequest request)
        {
            var apiUser = await GetApiUserAsync();
            if (apiUser == null)
            {
                return NotFound();
            }

            var stored = await _youthServiceListingRepository.GetByIdAsync(id);
            if (stored == null)
            {
                return NotFound();
            }

            var requestedOwnerUniqueId = request?.OwnerUniqueId?.Trim();
            if (string.IsNullOrWhiteSpace(requestedOwnerUniqueId))
            {
                return BadRequest("OwnerUniqueId is required.");
            }

            var newOwner = await _userRepository.GetByUniqueIdAsync(requestedOwnerUniqueId);
            if (newOwner == null)
            {
                return BadRequest("Specified owner does not exist.");
            }

            stored.CreatedByUniqueId = newOwner.UniqueId;
            stored.ModifiedByUniqueId = apiUser.UniqueId;
            stored.ModifiedUtc = DateTime.UtcNow;

            var saved = await _youthServiceListingRepository.UpsertAsync(stored);
            await WriteAudit(apiUser, saved.Id.ToString("D"), saved.Name, "Reassigned youth service listing owner.");
            return Ok(
                YouthServiceListingPresentation.FromStorageModel(saved, CanEdit(apiUser, saved.CreatedByUniqueId))
            );
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

        private static string ExtractSurname(string name)
        {
            var trimmed = (name ?? string.Empty).Trim();
            var lastSpace = trimmed.LastIndexOf(' ');
            return lastSpace >= 0 ? trimmed.Substring(lastSpace + 1) : trimmed;
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
    }
}
