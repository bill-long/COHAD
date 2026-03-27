using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Web.Services
{
    public record ImageUploadResult(
        string BlobPath,
        string FinalDisplayName,
        string ContentType,
        long SizeBytes);

    public interface IImageUploadHelper
    {
        /// <summary>
        /// Attempts PNG-to-JPEG conversion, then uploads the (possibly converted) image to blob storage.
        /// Returns the final blob path, display name, content type, and size.
        /// </summary>
        Task<ImageUploadResult> ConvertAndUploadAsync(
            IFormFile file,
            string extension,
            string blobPathPrefix,
            string safeBaseName);
    }

    public class ImageUploadHelper : IImageUploadHelper
    {
        private readonly IImageConversionService _imageConversion;
        private readonly IDocumentFileStore _fileStore;

        public ImageUploadHelper(IImageConversionService imageConversion, IDocumentFileStore fileStore)
        {
            _imageConversion = imageConversion;
            _fileStore = fileStore;
        }

        public async Task<ImageUploadResult> ConvertAndUploadAsync(
            IFormFile file,
            string extension,
            string blobPathPrefix,
            string safeBaseName)
        {
            ImageConversionResult? converted = null;

            // Only attempt conversion for extensions that TryConvertToJpeg supports.
            if (string.Equals(extension, ".png", System.StringComparison.OrdinalIgnoreCase))
            {
                await using var stream = file.OpenReadStream();
                converted = _imageConversion.TryConvertToJpeg(stream, extension);
            }

            if (converted != null)
            {
                extension = converted.Extension;
            }

            var finalDisplayName = $"{safeBaseName}{extension.ToLowerInvariant()}";
            var blobPath = $"{blobPathPrefix}/{finalDisplayName}";
            var trustedContentType = converted?.ContentType ?? ImageContentTypes.FromExtension(extension);

            if (converted != null)
            {
                await using var ms = new MemoryStream(converted.Data);
                await _fileStore.UploadAsync(blobPath, ms, trustedContentType);
            }
            else
            {
                await using var stream = file.OpenReadStream();
                await _fileStore.UploadAsync(blobPath, stream, trustedContentType);
            }

            return new ImageUploadResult(blobPath, finalDisplayName, trustedContentType,
                converted?.Data.Length ?? file.Length);
        }
    }
}
