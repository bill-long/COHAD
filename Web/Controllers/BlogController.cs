using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
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
    public class BlogController : ControllerBase
    {
        private static readonly HashSet<string> AllowedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png",
            ".jpg",
            ".jpeg",
            ".gif",
            ".webp"
        };

        private readonly IUserRepository _userRepository;
        private readonly IBlogPostRepository _blogPostRepository;
        private readonly IBlogCommentRepository _blogCommentRepository;
        private readonly IDocumentFileStore _documentFileStore;
        private readonly IAuditLogRepository _auditLogRepository;
        private readonly DocumentStorageOptions _storageOptions;

        public BlogController(
            IUserRepository userRepository,
            IBlogPostRepository blogPostRepository,
            IBlogCommentRepository blogCommentRepository,
            IDocumentFileStore documentFileStore,
            IAuditLogRepository auditLogRepository,
            IOptions<DocumentStorageOptions> storageOptions)
        {
            _userRepository = userRepository;
            _blogPostRepository = blogPostRepository;
            _blogCommentRepository = blogCommentRepository;
            _documentFileStore = documentFileStore;
            _auditLogRepository = auditLogRepository;
            _storageOptions = storageOptions.Value;
        }

        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> GetPublished()
        {
            var posts = await _blogPostRepository.GetPublishedAsync(DateTime.UtcNow);
            var payload = posts
                .Select(BlogPostCard.FromStorageModel)
                .ToList();
            return Ok(payload);
        }

        [HttpGet("{segment}")]
        [AllowAnonymous]
        public async Task<IActionResult> GetBySegment(string segment)
        {
            var stored = await _blogPostRepository.GetByRouteSegmentAsync(segment);
            if (stored == null)
            {
                return NotFound();
            }

            return Ok(BlogPostDetail.FromStorageModel(stored));
        }

        [HttpGet("{segment}/image")]
        [AllowAnonymous]
        public async Task<IActionResult> DownloadFeaturedImage(string segment)
        {
            var stored = await _blogPostRepository.GetByRouteSegmentAsync(segment);
            if (stored == null || string.IsNullOrWhiteSpace(stored.FeaturedImageBlobPath))
            {
                return NotFound();
            }

            var file = await _documentFileStore.DownloadAsync(stored.FeaturedImageBlobPath);
            if (file == null)
            {
                return NotFound();
            }

            var contentType = string.IsNullOrWhiteSpace(file.ContentType)
                ? "application/octet-stream"
                : file.ContentType;
            return File(file.Stream, contentType);
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

            if (!HasBlogManagementAccess(apiUser))
            {
                return Forbid();
            }

            var all = await _blogPostRepository.GetAllAsync();
            var posts = all
                .OrderByDescending(p => p.PublishUtc)
                .Select(BlogPostDetail.FromStorageModel)
                .ToList();
            return Ok(posts);
        }

        [HttpPost("manage")]
        [Authorize]
        public async Task<IActionResult> UpsertManage([FromForm] BlogPostUpsertRequest request)
        {
            var apiUser = await GetApiUserAsync();
            if (apiUser == null)
            {
                return NotFound();
            }

            if (!HasBlogManagementAccess(apiUser))
            {
                return Forbid();
            }

            if (request == null)
            {
                return BadRequest("Request body is required.");
            }

            if (string.IsNullOrWhiteSpace(request.Title))
            {
                return BadRequest("Title is required.");
            }

            if (string.IsNullOrWhiteSpace(request.Content))
            {
                return BadRequest("Content is required.");
            }

            var now = DateTime.UtcNow;
            var isCreate = request.Id == null;
            BlogPost post;
            BlogPostReadResult updateRead = null;

            if (isCreate)
            {
                post = new BlogPost
                {
                    Id = Guid.NewGuid(),
                    CreatedByUniqueId = apiUser.UniqueId,
                    CreatedUtc = now
                };
            }
            else
            {
                updateRead = await _blogPostRepository.ReadAsync(request.Id!.Value);
                if (updateRead == null)
                {
                    return NotFound();
                }

                post = updateRead.Post;
            }

            // Handle featured image upload
            if (request.FeaturedImage != null && request.FeaturedImage.Length > 0)
            {
                if (request.FeaturedImage.Length > _storageOptions.MaxUploadBytes)
                {
                    return BadRequest($"File size exceeds max allowed size of {_storageOptions.MaxUploadBytes} bytes.");
                }

                var extension = Path.GetExtension(request.FeaturedImage.FileName);
                if (string.IsNullOrWhiteSpace(extension) || !AllowedImageExtensions.Contains(extension))
                {
                    return BadRequest("Featured image must be an image file (PNG, JPEG, GIF, or WebP).");
                }

                var safeBaseName = SanitizeFileName(Path.GetFileNameWithoutExtension(request.FeaturedImage.FileName));
                if (string.IsNullOrWhiteSpace(safeBaseName))
                {
                    return BadRequest("Uploaded file name is invalid.");
                }

                var finalDisplayName = $"{safeBaseName}{extension.ToLowerInvariant()}";
                var blobPath = $"blog/{post.Id:D}/{finalDisplayName}";

                await using (var stream = request.FeaturedImage.OpenReadStream())
                {
                    await _documentFileStore.UploadAsync(blobPath, stream, request.FeaturedImage.ContentType);
                }

                if (!string.IsNullOrWhiteSpace(post.FeaturedImageBlobPath) &&
                    !string.Equals(post.FeaturedImageBlobPath, blobPath, StringComparison.OrdinalIgnoreCase))
                {
                    await _documentFileStore.DeleteAsync(post.FeaturedImageBlobPath);
                }

                post.FeaturedImageBlobPath = blobPath;
                post.FeaturedImageDisplayName = finalDisplayName;
                post.FeaturedImageContentType = request.FeaturedImage.ContentType;
                post.FeaturedImageSizeBytes = request.FeaturedImage.Length;
            }
            else if (request.RemoveFeaturedImage && !string.IsNullOrWhiteSpace(post.FeaturedImageBlobPath))
            {
                await _documentFileStore.DeleteAsync(post.FeaturedImageBlobPath);
                post.FeaturedImageBlobPath = null;
                post.FeaturedImageDisplayName = null;
                post.FeaturedImageContentType = null;
                post.FeaturedImageSizeBytes = null;
            }

            post.Title = request.Title.Trim();
            post.Content = request.Content;
            post.Excerpt = string.IsNullOrWhiteSpace(request.Excerpt)
                ? GenerateExcerpt(request.Content)
                : request.Excerpt.Trim();
            post.PublishUtc = request.PublishUtc.HasValue
                ? NormalizeToUtc(request.PublishUtc.Value)
                : now;
            post.AuthorDisplayName = $"{apiUser.GivenName ?? string.Empty} {apiUser.Surname ?? string.Empty}".Trim();
            post.ModifiedByUniqueId = apiUser.UniqueId;
            post.ModifiedUtc = now;

            var allPosts = await _blogPostRepository.GetAllAsync();
            post.PublicSlug = BlogUrlSlug.EnsureUniquePublicSlug(
                post.Id,
                post.PublishUtc,
                post.Title,
                allPosts).ToLowerInvariant();

            BlogPost saved;
            if (isCreate)
            {
                saved = await _blogPostRepository.UpsertAsync(post);
            }
            else
            {
                try
                {
                    saved = await _blogPostRepository.ReplaceAsync(post, updateRead!.ETag);
                }
                catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    return NotFound();
                }
                catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
                {
                    return StatusCode(StatusCodes.Status409Conflict,
                        "Unable to save post due to concurrent updates. Please refresh and try again.");
                }
            }

            await _auditLogRepository.AddAsync(new NewAuditLogEntry
            {
                Id = Guid.NewGuid(),
                SubjectId = saved.Id.ToString("D"),
                SubjectName = saved.Title,
                Action = isCreate ? "Created blog post." : "Updated blog post.",
                Time = DateTime.UtcNow,
                UserDisplayName = $"{apiUser.GivenName ?? string.Empty} {apiUser.Surname ?? string.Empty}",
                UserId = apiUser.UniqueId
            });

            return Ok(BlogPostDetail.FromStorageModel(saved));
        }

        [HttpPost("upload-image")]
        [Authorize]
        public async Task<IActionResult> UploadImage(IFormFile file)
        {
            var apiUser = await GetApiUserAsync();
            if (apiUser == null)
            {
                return NotFound();
            }

            if (!HasBlogManagementAccess(apiUser))
            {
                return Forbid();
            }

            if (file == null || file.Length == 0)
            {
                return BadRequest("No file provided.");
            }

            if (file.Length > _storageOptions.MaxUploadBytes)
            {
                return BadRequest($"File size exceeds max allowed size of {_storageOptions.MaxUploadBytes} bytes.");
            }

            var extension = Path.GetExtension(file.FileName);
            if (string.IsNullOrWhiteSpace(extension) || !AllowedImageExtensions.Contains(extension))
            {
                return BadRequest("Image must be PNG, JPEG, GIF, or WebP.");
            }

            var safeBaseName = SanitizeFileName(Path.GetFileNameWithoutExtension(file.FileName));
            if (string.IsNullOrWhiteSpace(safeBaseName))
            {
                return BadRequest("File name is invalid.");
            }

            var finalName = $"{safeBaseName}{extension.ToLowerInvariant()}";
            var inlinePath = $"{Guid.NewGuid():D}/{finalName}";
            var blobPath = $"blog/images/{inlinePath}";

            await using (var stream = file.OpenReadStream())
            {
                await _documentFileStore.UploadAsync(blobPath, stream, file.ContentType);
            }

            return Ok(new { url = $"/api/blog/images/{inlinePath}" });
        }

        [HttpGet("images/{**blobPath}")]
        [AllowAnonymous]
        public async Task<IActionResult> ServeInlineImage(string blobPath)
        {
            if (string.IsNullOrWhiteSpace(blobPath) ||
                blobPath.Contains("..") ||
                Path.IsPathRooted(blobPath))
            {
                return NotFound();
            }

            var fullBlobPath = $"blog/images/{blobPath}";
            var file = await _documentFileStore.DownloadAsync(fullBlobPath);
            if (file == null)
            {
                return NotFound();
            }

            var contentType = string.IsNullOrWhiteSpace(file.ContentType)
                ? "application/octet-stream"
                : file.ContentType;
            return File(file.Stream, contentType);
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

            if (!HasBlogManagementAccess(apiUser))
            {
                return Forbid();
            }

            var stored = await _blogPostRepository.GetByIdAsync(id);
            if (stored == null)
            {
                return NotFound();
            }

            if (!string.IsNullOrWhiteSpace(stored.FeaturedImageBlobPath))
            {
                await _documentFileStore.DeleteAsync(stored.FeaturedImageBlobPath);
            }

            await _blogCommentRepository.DeleteByBlogPostCascadeAsync(id);
            await _blogPostRepository.DeleteAsync(id);
            await _auditLogRepository.AddAsync(new NewAuditLogEntry
            {
                Id = Guid.NewGuid(),
                SubjectId = stored.Id.ToString("D"),
                SubjectName = stored.Title,
                Action = "Deleted blog post.",
                Time = DateTime.UtcNow,
                UserDisplayName = $"{apiUser.GivenName ?? string.Empty} {apiUser.Surname ?? string.Empty}",
                UserId = apiUser.UniqueId
            });

            return Ok();
        }

        [HttpGet("{segment}/comments")]
        [AllowAnonymous]
        public async Task<IActionResult> GetComments(string segment)
        {
            var stored = await _blogPostRepository.GetByRouteSegmentAsync(segment);
            if (stored == null)
            {
                return NotFound();
            }

            var comments = await _blogCommentRepository.GetByBlogPostIdAsync(stored.Id);

            Models.User apiUser = null;
            if (User.Identity?.IsAuthenticated == true)
            {
                apiUser = await GetApiUserAsync();
            }

            var isAdmin = apiUser?.Roles?.Contains(Models.User.Role.Administrator) == true;

            var payload = comments
                .Select(c => BlogCommentPresentation.FromStorageModel(c,
                    canDelete: isAdmin || (apiUser != null && c.AuthorUniqueId == apiUser.UniqueId)))
                .ToList();

            return Ok(payload);
        }

        [HttpPost("{segment}/comments")]
        [Authorize(Policy = "Resident")]
        public async Task<IActionResult> AddComment(string segment, [FromBody] BlogCommentCreateRequest request)
        {
            var stored = await _blogPostRepository.GetByRouteSegmentAsync(segment);
            if (stored == null)
            {
                return NotFound();
            }

            var apiUser = await GetApiUserAsync();
            if (apiUser == null)
            {
                return NotFound();
            }

            if (request == null || string.IsNullOrWhiteSpace(request.Content))
            {
                return BadRequest("Comment content is required.");
            }

            if (request.Content.Length > 2000)
            {
                return BadRequest("Comment must be 2000 characters or fewer.");
            }

            var comment = new BlogComment
            {
                Id = Guid.NewGuid(),
                BlogPostId = stored.Id,
                AuthorUniqueId = apiUser.UniqueId,
                AuthorDisplayName = $"{apiUser.GivenName ?? string.Empty} {apiUser.Surname ?? string.Empty}".Trim(),
                Content = request.Content.Trim(),
                CreatedUtc = DateTime.UtcNow
            };

            var saved = await _blogCommentRepository.UpsertAsync(comment);
            return Ok(BlogCommentPresentation.FromStorageModel(saved, canDelete: true));
        }

        [HttpDelete("{segment}/comments/{commentId:guid}")]
        [Authorize]
        public async Task<IActionResult> DeleteComment(string segment, Guid commentId)
        {
            var stored = await _blogPostRepository.GetByRouteSegmentAsync(segment);
            if (stored == null)
            {
                return NotFound();
            }

            var comment = await _blogCommentRepository.GetByIdAsync(commentId);
            if (comment == null || comment.BlogPostId != stored.Id)
            {
                return NotFound();
            }

            var apiUser = await GetApiUserAsync();
            if (apiUser == null)
            {
                return NotFound();
            }

            var isAdmin = apiUser.Roles?.Contains(Models.User.Role.Administrator) == true;
            if (!isAdmin && comment.AuthorUniqueId != apiUser.UniqueId)
            {
                return Forbid();
            }

            await _blogCommentRepository.DeleteAsync(commentId);
            return Ok();
        }

        private async Task<Models.User> GetApiUserAsync()
        {
            var uniqueId = Models.User.GetUniqueIdFromClaims(User.Claims);
            return await _userRepository.GetByUniqueIdAsync(uniqueId);
        }

        /// <summary>
        /// Blog management is available to any authenticated user who has the Resident role
        /// plus at least one additional role (same pattern as event management).
        /// </summary>
        private static bool HasBlogManagementAccess(Models.User user)
        {
            if (user?.Roles == null || user.Roles.Count == 0)
            {
                return false;
            }

            return user.Roles.Contains(Models.User.Role.Resident) &&
                   user.Roles.Any(r => r != Models.User.Role.Resident);
        }

        /// <summary>
        /// Strips Markdown syntax and collapses whitespace to produce a plain-text excerpt up to 300 characters.
        /// </summary>
        internal static string GenerateExcerpt(string markdown)
        {
            return MarkdownPlainText.ToPlainText(markdown);
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

        private static string SanitizeFileName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
            return cleaned;
        }
    }
}
