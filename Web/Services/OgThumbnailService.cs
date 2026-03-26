using System;
using System.IO;
using SkiaSharp;

namespace Web.Services
{
    public interface IOgThumbnailService
    {
        byte[] GenerateThumbnail(Stream sourceImageStream);
    }

    public class SkiaSharpOgThumbnailService : IOgThumbnailService
    {
        internal const int TargetWidth = 1200;
        internal const int TargetHeight = 630;
        private const int JpegQuality = 80;

        public byte[] GenerateThumbnail(Stream sourceImageStream)
        {
            using var original = SKBitmap.Decode(sourceImageStream);
            if (original == null)
            {
                throw new InvalidOperationException("Unable to decode image.");
            }

            // Scale so the image covers the target rect, then center-crop.
            var scaleX = (float)TargetWidth / original.Width;
            var scaleY = (float)TargetHeight / original.Height;
            var scale = Math.Max(scaleX, scaleY);

            var scaledWidth = (int)Math.Round(original.Width * scale);
            var scaledHeight = (int)Math.Round(original.Height * scale);

            using var scaled = original.Resize(new SKImageInfo(scaledWidth, scaledHeight), new SKSamplingOptions(SKCubicResampler.Mitchell));
            if (scaled == null)
            {
                throw new InvalidOperationException("Unable to resize image.");
            }

            var cropX = (scaledWidth - TargetWidth) / 2;
            var cropY = (scaledHeight - TargetHeight) / 2;
            var cropRect = new SKRectI(cropX, cropY, cropX + TargetWidth, cropY + TargetHeight);

            using var subset = new SKBitmap();
            if (!scaled.ExtractSubset(subset, cropRect))
            {
                throw new InvalidOperationException("Unable to crop image.");
            }

            using var image = SKImage.FromBitmap(subset);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
            return data.ToArray();
        }
    }
}
