using System.IO;
using SkiaSharp;
using Web.Services;

namespace Web.UnitTests;

public sealed class ImageConversionServiceTests
{
    private readonly SkiaSharpImageConversionService _service = new();

    [Fact]
    public void TryConvertToJpeg_converts_png_to_jpeg()
    {
        var pngBytes = CreateMinimalPng();
        using var stream = new MemoryStream(pngBytes);

        var result = _service.TryConvertToJpeg(stream, ".png");

        Assert.NotNull(result);
        Assert.Equal(".jpg", result!.Extension);
        Assert.Equal("image/jpeg", result.ContentType);
        Assert.True(result.Data.Length > 0);

        // Verify the output is valid JPEG (starts with FF D8)
        Assert.Equal(0xFF, result.Data[0]);
        Assert.Equal(0xD8, result.Data[1]);
    }

    [Fact]
    public void TryConvertToJpeg_is_case_insensitive_on_extension()
    {
        var pngBytes = CreateMinimalPng();
        using var stream = new MemoryStream(pngBytes);

        var result = _service.TryConvertToJpeg(stream, ".PNG");

        Assert.NotNull(result);
        Assert.Equal(".jpg", result!.Extension);
    }

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".jpeg")]
    [InlineData(".gif")]
    [InlineData(".webp")]
    public void TryConvertToJpeg_returns_null_for_non_png_extensions(string extension)
    {
        using var stream = new MemoryStream(new byte[] { 1, 2, 3 });

        var result = _service.TryConvertToJpeg(stream, extension);

        Assert.Null(result);
    }

    [Fact]
    public void TryConvertToJpeg_returns_null_for_corrupt_png()
    {
        using var stream = new MemoryStream(new byte[] { 0, 0, 0, 0 });

        var result = _service.TryConvertToJpeg(stream, ".png");

        Assert.Null(result);
    }

    [Fact]
    public void TryConvertToJpeg_produces_valid_jpeg_from_photo_like_png()
    {
        // Create a PNG with varied pixel data (simulating a photo) where JPEG compression excels
        var pngBytes = CreatePhotoPng(400, 300);
        using var stream = new MemoryStream(pngBytes);

        var result = _service.TryConvertToJpeg(stream, ".png");

        Assert.NotNull(result);
        Assert.True(result!.Data.Length > 0);
        // Verify output is valid JPEG
        Assert.Equal(0xFF, result.Data[0]);
        Assert.Equal(0xD8, result.Data[1]);
    }

    private static byte[] CreateMinimalPng()
    {
        return CreatePng(10, 10);
    }

    private static byte[] CreatePng(int width, int height)
    {
        using var bmp = new SKBitmap(width, height);
        bmp.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static byte[] CreatePhotoPng(int width, int height)
    {
        using var bmp = new SKBitmap(width, height);
        var rng = new System.Random(42);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                bmp.SetPixel(x, y, new SKColor(
                    (byte)rng.Next(256),
                    (byte)rng.Next(256),
                    (byte)rng.Next(256)));
            }
        }

        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
