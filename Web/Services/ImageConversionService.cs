using System.IO;
using SkiaSharp;

namespace Web.Services
{
    public class ImageConversionResult
    {
        public byte[] Data { get; init; }
        public string Extension { get; init; }
        public string ContentType { get; init; }
    }

    public interface IImageConversionService
    {
        ImageConversionResult TryConvertToJpeg(Stream source, string originalExtension);
    }

    public class SkiaSharpImageConversionService : IImageConversionService
    {
        private const int JpegQuality = 85;

        public ImageConversionResult TryConvertToJpeg(Stream source, string originalExtension)
        {
            if (!string.Equals(originalExtension, ".png", System.StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            using var bitmap = SKBitmap.Decode(source);
            if (bitmap == null)
            {
                return null;
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
            if (data == null)
            {
                return null;
            }

            return new ImageConversionResult
            {
                Data = data.ToArray(),
                Extension = ".jpg",
                ContentType = "image/jpeg"
            };
        }
    }
}
