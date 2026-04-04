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
    [Route("api/document-folder")]
    [ApiController]
    [Authorize]
    public class DocumentFolderController : ControllerBase
    {
        private readonly IUserRepository _userRepository;
        private readonly IDocumentFolderRepository _folderRepository;
        private readonly IDocumentRepository _documentRepository;
        private readonly IAuditLogRepository _auditLogRepository;

        public DocumentFolderController(
            IUserRepository userRepository,
            IDocumentFolderRepository folderRepository,
            IDocumentRepository documentRepository,
            IAuditLogRepository auditLogRepository)
        {
            _userRepository = userRepository;
            _folderRepository = folderRepository;
            _documentRepository = documentRepository;
            _auditLogRepository = auditLogRepository;
        }

        [HttpGet]
        public async Task<IActionResult> Get()
        {
            var apiUser = await GetApiUserAsync();
            if (apiUser == null)
            {
                return NotFound();
            }

            if (!apiUser.HasResidentAccess)
            {
                return Forbid();
            }

            var folders = await _folderRepository.GetAllAsync();
            var documents = await _documentRepository.GetAllAsync();
            var countsByFolder = documents
                .Where(d => d.FolderId != null)
                .GroupBy(d => d.FolderId.Value)
                .ToDictionary(g => g.Key, g => g.Count());

            var payload = folders
                .OrderBy(f => f.SortOrder)
                .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => DocumentFolderSummary.FromStorageModel(
                    f, countsByFolder.GetValueOrDefault(f.Id, 0)))
                .ToList();

            return Ok(payload);
        }

        [HttpPost]
        [Authorize(Policy = "Administrator")]
        public async Task<IActionResult> Create([FromBody] CreateFolderRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.Name))
            {
                return BadRequest("Folder name is required.");
            }

            var name = request.Name.Trim();
            if (name.Length > 100)
            {
                return BadRequest("Folder name must be 100 characters or fewer.");
            }

            var existing = await _folderRepository.GetAllAsync();
            if (existing.Any(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                return Conflict("A folder with this name already exists.");
            }

            var maxSort = existing.Count > 0 ? existing.Max(f => f.SortOrder) : -1;
            var folder = new DocumentFolder
            {
                Id = Guid.NewGuid(),
                Name = name,
                SortOrder = maxSort + 1,
                CreatedUtc = DateTime.UtcNow
            };

            await _folderRepository.UpsertAsync(folder);

            var apiUser = await GetApiUserAsync();
            await _auditLogRepository.AddAsync(new NewAuditLogEntry
            {
                Id = Guid.NewGuid(),
                SubjectId = folder.Id.ToString("D"),
                SubjectName = folder.Name,
                Action = "Created document folder.",
                Time = DateTime.UtcNow,
                UserDisplayName = $"{apiUser?.GivenName ?? ""} {apiUser?.Surname ?? ""}",
                UserId = apiUser?.UniqueId
            });

            return Ok(DocumentFolderSummary.FromStorageModel(folder, 0));
        }

        [HttpPut("{id:guid}")]
        [Authorize(Policy = "Administrator")]
        public async Task<IActionResult> Update(Guid id, [FromBody] UpdateFolderRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.Name))
            {
                return BadRequest("Folder name is required.");
            }

            var name = request.Name.Trim();
            if (name.Length > 100)
            {
                return BadRequest("Folder name must be 100 characters or fewer.");
            }

            var folder = await _folderRepository.GetByIdAsync(id);
            if (folder == null)
            {
                return NotFound();
            }

            var allFolders = await _folderRepository.GetAllAsync();
            if (allFolders.Any(f => f.Id != id && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                return Conflict("A folder with this name already exists.");
            }

            folder.Name = name;
            if (request.SortOrder.HasValue)
            {
                folder.SortOrder = request.SortOrder.Value;
            }

            await _folderRepository.UpsertAsync(folder);

            var apiUser = await GetApiUserAsync();
            await _auditLogRepository.AddAsync(new NewAuditLogEntry
            {
                Id = Guid.NewGuid(),
                SubjectId = folder.Id.ToString("D"),
                SubjectName = folder.Name,
                Action = "Updated document folder.",
                Time = DateTime.UtcNow,
                UserDisplayName = $"{apiUser?.GivenName ?? ""} {apiUser?.Surname ?? ""}",
                UserId = apiUser?.UniqueId
            });

            var documents = await _documentRepository.GetAllAsync();
            var count = documents.Count(d => d.FolderId == id);
            return Ok(DocumentFolderSummary.FromStorageModel(folder, count));
        }

        [HttpDelete("{id:guid}")]
        [Authorize(Policy = "Administrator")]
        public async Task<IActionResult> Delete(Guid id)
        {
            var folder = await _folderRepository.GetByIdAsync(id);
            if (folder == null)
            {
                return NotFound();
            }

            var documents = await _documentRepository.GetAllAsync();
            var count = documents.Count(d => d.FolderId == id);
            if (count > 0)
            {
                return Conflict($"Cannot delete folder \"{folder.Name}\" because it contains {count} document(s). Remove or reassign them first.");
            }

            await _folderRepository.DeleteAsync(id);

            var apiUser = await GetApiUserAsync();
            await _auditLogRepository.AddAsync(new NewAuditLogEntry
            {
                Id = Guid.NewGuid(),
                SubjectId = folder.Id.ToString("D"),
                SubjectName = folder.Name,
                Action = "Deleted document folder.",
                Time = DateTime.UtcNow,
                UserDisplayName = $"{apiUser?.GivenName ?? ""} {apiUser?.Surname ?? ""}",
                UserId = apiUser?.UniqueId
            });

            return Ok();
        }

        [HttpPut("reorder")]
        [Authorize(Policy = "Administrator")]
        public async Task<IActionResult> Reorder([FromBody] List<ReorderItem> items)
        {
            if (items == null || items.Count == 0)
            {
                return BadRequest("At least one item is required.");
            }

            var folders = await _folderRepository.GetAllAsync();
            var lookup = folders.ToDictionary(f => f.Id);

            foreach (var item in items)
            {
                if (!lookup.TryGetValue(item.Id, out var folder))
                {
                    return NotFound($"Folder {item.Id} not found.");
                }

                folder.SortOrder = item.SortOrder;
                await _folderRepository.UpsertAsync(folder);
            }

            return Ok();
        }

        private async Task<User> GetApiUserAsync()
        {
            var uniqueId = Models.User.GetUniqueIdFromClaims(User.Claims);
            return await _userRepository.GetByUniqueIdAsync(uniqueId);
        }
    }
}
