using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Web.Models;
using Web.PresentationModels;
using Web.Services.Repositories;
using Web.UpdateModels;

namespace Web.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize(Policy = "Resident")]
    public class YouthServicesController : ControllerBase
    {
        private readonly IYouthServiceListingRepository _youthServiceListingRepository;
        private readonly IUserRepository _userRepository;
        private readonly IAuditLogRepository _auditLogRepository;

        public YouthServicesController(
            IYouthServiceListingRepository youthServiceListingRepository,
            IUserRepository userRepository,
            IAuditLogRepository auditLogRepository)
        {
            _youthServiceListingRepository = youthServiceListingRepository;
            _userRepository = userRepository;
            _auditLogRepository = auditLogRepository;
        }

        [HttpGet]
        public async Task<IActionResult> GetAll([FromQuery] string q = null, [FromQuery] string service = null)
        {
            var all = await _youthServiceListingRepository.GetAllAsync();
            var query = all.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(q))
            {
                var trimmed = q.Trim();
                query = query.Where(l =>
                    (l.Name?.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (l.ParentNote?.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ?? false));
            }

            if (!string.IsNullOrWhiteSpace(service))
            {
                var trimmedService = service.Trim();
                query = query.Where(l => l.Services?.Any(s => s.Equals(trimmedService, StringComparison.OrdinalIgnoreCase)) == true);
            }

            var payload = query
                .OrderBy(l => l.Name)
                .Select(YouthServiceListingPresentation.FromStorageModel)
                .ToList();
            return Ok(payload);
        }

        [HttpGet("{id:guid}")]
        public async Task<IActionResult> GetById(Guid id)
        {
            var listing = await _youthServiceListingRepository.GetByIdAsync(id);
            if (listing == null)
            {
                return NotFound();
            }

            return Ok(YouthServiceListingPresentation.FromStorageModel(listing));
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
                Services = NormalizeStringList(request.Services),
                BornYear = request.BornYear,
                Phone = request.Phone?.Trim(),
                ContactMethod = request.ContactMethod,
                Email = request.Email?.Trim(),
                Address = request.Address?.Trim(),
                ParentNote = request.ParentNote?.Trim(),
                CreatedByUniqueId = apiUser.UniqueId,
                ModifiedByUniqueId = apiUser.UniqueId,
                CreatedUtc = now,
                ModifiedUtc = now
            };

            var saved = await _youthServiceListingRepository.UpsertAsync(listing);
            await WriteAudit(apiUser, saved.Id.ToString("D"), saved.Name, "Created youth service listing.");
            return Ok(YouthServiceListingPresentation.FromStorageModel(saved));
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
            stored.Services = NormalizeStringList(request.Services);
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
            return Ok(YouthServiceListingPresentation.FromStorageModel(saved));
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

        private static List<string> NormalizeStringList(IEnumerable<string> values) =>
            (values ?? Enumerable.Empty<string>())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v)
            .ToList();

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

        private async Task<User> GetApiUserAsync()
        {
            var uniqueId = Models.User.GetUniqueIdFromClaims(User.Claims);
            return await _userRepository.GetByUniqueIdAsync(uniqueId);
        }

        private async Task WriteAudit(User apiUser, string subjectId, string subjectName, string action)
        {
            await _auditLogRepository.AddAsync(new NewAuditLogEntry
            {
                Id = Guid.NewGuid(),
                SubjectId = subjectId,
                SubjectName = subjectName,
                Action = action,
                Time = DateTime.UtcNow,
                UserDisplayName = $"{apiUser.GivenName ?? string.Empty} {apiUser.Surname ?? string.Empty}".Trim(),
                UserId = apiUser.UniqueId
            });
        }
    }
}
