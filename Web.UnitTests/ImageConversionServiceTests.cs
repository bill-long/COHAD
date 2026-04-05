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
        Assert.True(result.Data.Length >= 2);

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
        Assert.True(result!.Data.Length >= 2);
        // Verify output is valid JPEG
        Assert.Equal(0xFF, result.Data[0]);
        Assert.Equal(0xD8, result.Data[1]);
    }

    [Fact]
    public void TryConvertToJpeg_skips_png_with_alpha_transparency()
    {
        var pngBytes = CreateTransparentPng(100, 100);
        using var stream = new MemoryStream(pngBytes);

        var result = _service.TryConvertToJpeg(stream, ".png");

        Assert.Null(result);
    }

    [Fact]
    public void TryConvertToJpeg_skips_when_jpeg_is_not_smaller()
    {
        // A small solid-color PNG compresses very efficiently; JPEG will be larger.
        var pngBytes = CreatePng(10, 10);
        using var stream = new MemoryStream(pngBytes);

        var result = _service.TryConvertToJpeg(stream, ".png");

        Assert.Null(result);
    }

    private static byte[] CreateMinimalPng()
    {
        // Use photo-like PNG so JPEG conversion is actually smaller (passes size check).
        return CreatePhotoPng(100, 100);
    }

    private static byte[] CreatePng(int width, int height)
    {
        using var bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgb888x, SKAlphaType.Opaque));
        bmp.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static byte[] CreatePhotoPng(int width, int height)
    {
        using var bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgb888x, SKAlphaType.Opaque));
        var rng = new System.Random(42);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                bmp.SetPixel(x, y, new SKColor((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256)));
            }
        }

        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static byte[] CreateTransparentPng(int width, int height)
    {
        using var bmp = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var rng = new System.Random(99);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                bmp.SetPixel(
                    x,
                    y,
                    new SKColor((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(128))
                ); // semi-transparent alpha
            }
        }

        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
