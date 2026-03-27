using System.IO;
using SkiaSharp;

namespace Web.Services
{
    public record ImageConversionResult(byte[] Data, string Extension, string ContentType);

    public interface IImageConversionService
    {
        ImageConversionResult? TryConvertToJpeg(Stream source, string originalExtension);
    }

    public class SkiaSharpImageConversionService : IImageConversionService
    {
        private const int JpegQuality = 85;

        /// <summary>Max total pixels (width * height) to decode. Prevents decompression bombs.</summary>
        internal const int MaxPixels = 40_000_000; // ~40 MP, ~160 MB at 32bpp

        public ImageConversionResult? TryConvertToJpeg(Stream source, string originalExtension)
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

            // Verify the stream is actually PNG, not just named .png.
            if (codec.EncodedFormat != SKEncodedImageFormat.Png)
            {
                return null;
            }

            var info = codec.Info;
            if ((long)info.Width * info.Height > MaxPixels)
            {
                return null;
            }

            // Skip conversion for PNGs with transparency — JPEG doesn't support alpha
            // and the result would have an unexpected black background.
            if (info.AlphaType != SKAlphaType.Opaque)
            {
                return null;
            }

            // Remember original size so we can compare after encoding.
            var originalSize = source.CanSeek ? source.Length : -1;

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

            // Skip conversion when JPEG is not smaller than the original PNG.
            if (originalSize > 0 && data.Size >= originalSize)
            {
                return null;
            }

            return new ImageConversionResult(data.ToArray(), ".jpg", "image/jpeg");
        }
    }
}
