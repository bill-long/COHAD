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

        /// <summary>Max total pixels (width * height) to decode. Prevents decompression bombs.</summary>
        internal const int MaxPixels = 100_000_000; // ~100 MP, ~400 MB at 32bpp

        public ImageConversionResult TryConvertToJpeg(Stream source, string originalExtension)
        {
            if (!string.Equals(originalExtension, ".png", System.StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // Check dimensions before full decode to guard against decompression bombs.
            using var codec = SKCodec.Create(source);
            if (codec == null)
            {
                return null;
            }

            var info = codec.Info;
            if ((long)info.Width * info.Height > MaxPixels)
            {
                return null;
            }

            using var bitmap = SKBitmap.Decode(codec);
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
