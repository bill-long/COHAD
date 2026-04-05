using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
    [Authorize]
    public class DocumentController : ControllerBase
    {
        private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf",
            ".doc",
            ".docx",
            ".xls",
            ".xlsx",
            ".txt",
        };

        private readonly IUserRepository _userRepository;
        private readonly IDocumentRepository _documentRepository;
        private readonly IDocumentFolderRepository _folderRepository;
        private readonly IDocumentFileStore _documentFileStore;
        private readonly IAuditLogRepository _auditLogRepository;
        private readonly DocumentListCache _listCache;
        private readonly DocumentStorageOptions _storageOptions;

        public DocumentController(
            IUserRepository userRepository,
            IDocumentRepository documentRepository,
            IDocumentFolderRepository folderRepository,
            IDocumentFileStore documentFileStore,
            IAuditLogRepository auditLogRepository,
            DocumentListCache listCache,
            IOptions<DocumentStorageOptions> storageOptions
        )
        {
            _userRepository = userRepository;
            _documentRepository = documentRepository;
            _folderRepository = folderRepository;
            _documentFileStore = documentFileStore;
            _auditLogRepository = auditLogRepository;
            _listCache = listCache;
            _storageOptions = storageOptions.Value;
        }

        [HttpGet]
        public async Task<IActionResult> Get()
        {
            var apiUser = await GetApiUserAsync();
            if (apiUser == null)
            {
                return NotFound();
            }

            if (!HasResidentReadAccess(apiUser))
            {
                return Forbid();
            }

            var docs = await _listCache.GetAllDocumentsAsync();
            var folders = await _listCache.GetAllFoldersAsync();
            var folderLookup = folders.ToDictionary(f => f.Id, f => f.Name);
            var payload = docs.OrderByDescending(d => d.CreatedUtc)
                .Select(d =>
                    ResidentDocumentSummary.FromStorageModel(
                        d,
                        d.FolderId != null && folderLookup.TryGetValue(d.FolderId.Value, out var name) ? name : null
                    )
                )
                .ToList();

            return _listCache.OkWithETag(payload, Request, Response, DocumentListCache.DocumentsResponseKey);
        }

        [HttpGet("{id:guid}")]
        public async Task<IActionResult> Download(Guid id)
        {
            var apiUser = await GetApiUserAsync();
            if (apiUser == null)
            {
                return NotFound();
            }

            if (!HasResidentReadAccess(apiUser))
            {
                return Forbid();
            }

            var stored = await _documentRepository.GetByIdAsync(id);
            if (stored == null)
            {
                return NotFound();
            }

            var file = await _documentFileStore.DownloadAsync(stored.BlobPath);
            if (file == null)
            {
                return NotFound();
            }

            var contentType = string.IsNullOrWhiteSpace(file.ContentType)
                ? "application/octet-stream"
                : file.ContentType;
            return File(file.Stream, contentType, stored.DisplayName);
        }

        [HttpPost]
        [Authorize(Policy = "Administrator")]
        public async Task<IActionResult> Upload([FromForm] DocumentUploadRequest request)
        {
            if (request?.File == null || request.File.Length <= 0)
            {
                return BadRequest("A file is required.");
            }

            if (request.File.Length > _storageOptions.MaxUploadBytes)
            {
                return BadRequest($"File size exceeds max allowed size of {_storageOptions.MaxUploadBytes} bytes.");
            }

            var extension = Path.GetExtension(request.File.FileName);
            if (string.IsNullOrWhiteSpace(extension) || !AllowedExtensions.Contains(extension))
            {
                return BadRequest("Unsupported file type.");
            }

            var apiUser = await GetApiUserAsync();
            if (apiUser == null)
            {
                return NotFound();
            }

            string folderName = null;
            if (request.FolderId.HasValue)
            {
                var folder = await _folderRepository.GetByIdAsync(request.FolderId.Value);
                if (folder == null)
                {
                    return BadRequest("The specified folder does not exist.");
                }

                folderName = folder.Name;
            }

            var id = Guid.NewGuid();
            var rawDisplayName = string.IsNullOrWhiteSpace(request.DisplayName)
                ? Path.GetFileNameWithoutExtension(request.File.FileName)
                : request.DisplayName.Trim();
            // Strip any extension the user typed (e.g. "Rules.pdf") so we don't duplicate the file extension ("Rules.pdf.pdf").
            var safeBaseName = SanitizeFileName(Path.GetFileNameWithoutExtension(rawDisplayName));
            if (string.IsNullOrWhiteSpace(safeBaseName))
            {
                return BadRequest("Display name is required.");
            }

            var finalDisplayName = $"{safeBaseName}{extension.ToLowerInvariant()}";
            var blobPath = $"documents/{id:D}/{finalDisplayName}";

            await using (var stream = request.File.OpenReadStream())
            {
                await _documentFileStore.UploadAsync(blobPath, stream, request.File.ContentType);
            }

            var created = new ResidentDocument
            {
                Id = id,
                DisplayName = finalDisplayName,
                OriginalFileName = request.File.FileName,
                BlobPath = blobPath,
                ContentType = request.File.ContentType,
                SizeBytes = request.File.Length,
                UploadedByUniqueId = apiUser.UniqueId,
                CreatedUtc = DateTime.UtcNow,
                FolderId = request.FolderId,
            };

            await _documentRepository.UpsertAsync(created);
            _listCache.InvalidateDocuments();
            await _auditLogRepository.AddAsync(
                new NewAuditLogEntry
                {
                    Id = Guid.NewGuid(),
                    SubjectId = created.Id.ToString("D"),
                    SubjectName = created.DisplayName,
                    Action = "Uploaded resident document.",
                    Time = DateTime.UtcNow,
                    UserDisplayName = $"{apiUser.GivenName ?? ""} {apiUser.Surname ?? ""}",
                    UserId = apiUser.UniqueId,
                }
            );

            return Ok(ResidentDocumentSummary.FromStorageModel(created, folderName));
        }

        [HttpDelete("{id:guid}")]
        [Authorize(Policy = "Administrator")]
        public async Task<IActionResult> Delete(Guid id)
        {
            var apiUser = await GetApiUserAsync();
            if (apiUser == null)
            {
                return NotFound();
            }

            var stored = await _documentRepository.GetByIdAsync(id);
            if (stored == null)
            {
                return NotFound();
            }

            await _documentFileStore.DeleteAsync(stored.BlobPath);
            await _documentRepository.DeleteAsync(id);
            _listCache.InvalidateDocuments();
            await _auditLogRepository.AddAsync(
                new NewAuditLogEntry
                {
                    Id = Guid.NewGuid(),
                    SubjectId = stored.Id.ToString("D"),
                    SubjectName = stored.DisplayName,
                    Action = "Deleted resident document.",
                    Time = DateTime.UtcNow,
                    UserDisplayName = $"{apiUser.GivenName ?? ""} {apiUser.Surname ?? ""}",
                    UserId = apiUser.UniqueId,
                }
            );

            return Ok();
        }

        private async Task<User> GetApiUserAsync()
        {
            var uniqueId = Models.User.GetUniqueIdFromClaims(User.Claims);
            return await _userRepository.GetByUniqueIdAsync(uniqueId);
        }

        private static bool HasResidentReadAccess(User user)
        {
            return user.HasResidentAccess;
        }

        private static string SanitizeFileName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
            return cleaned;
        }
    }
}
