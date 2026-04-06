using Web.Services;
using Xunit;

namespace Web.UnitTests;

public class EmailMessageBuilderTests
{
    [Fact]
    public void InlineCss_MovesStyleBlockIntoInlineAttributes()
    {
        var html = "<html><head><style>p { color: red; }</style></head><body><p>Hello</p></body></html>";
        var result = EmailMessageBuilder.InlineCss(html);

        Assert.Contains("color: red", result);
        Assert.DoesNotContain("<style>", result);
    }

    [Fact]
    public void InlineCss_ReturnsOriginalWhenNoStyleBlock()
    {
        var html = "<p>No styles here</p>";
        var result = EmailMessageBuilder.InlineCss(html);

        Assert.Contains("No styles here", result);
    }

    [Fact]
    public void InlineCss_ReturnsEmptyForNull_PreservesWhitespace()
    {
        Assert.Equal("", EmailMessageBuilder.InlineCss(null));
        Assert.Equal("", EmailMessageBuilder.InlineCss(""));
        Assert.Equal("   ", EmailMessageBuilder.InlineCss("   "));
    }

    [Fact]
    public void InlineCss_HandlesMultipleSelectors()
    {
        var html =
            "<html><head><style>h1 { font-size: 24px; } p { margin: 0; }</style></head>"
            + "<body><h1>Title</h1><p>Body</p></body></html>";
        var result = EmailMessageBuilder.InlineCss(html);

        Assert.Contains("font-size: 24px", result);
        Assert.Contains("margin: 0", result);
    }

    [Fact]
    public void ExtractInlineImages_ReplacesDataUriWithCid()
    {
        var base64 = Convert.ToBase64String(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        var html = $"<p>Before</p><img src=\"data:image/png;base64,{base64}\"><p>After</p>";

        var result = EmailMessageBuilder.ExtractInlineImages(html);

        Assert.Contains("cid:", result.ProcessedHtml);
        Assert.DoesNotContain("data:image", result.ProcessedHtml);
        Assert.Single(result.Images);
        Assert.Equal("image0.png", result.Images[0].FileName);
    }

    [Fact]
    public void ExtractInlineImages_NoImages_ReturnsUnmodifiedHtml()
    {
        var html = "<p>No images</p>";
        var result = EmailMessageBuilder.ExtractInlineImages(html);

        Assert.Equal(html, result.ProcessedHtml);
        Assert.Empty(result.Images);
    }

    [Fact]
    public void InlineCss_ThenExtractInlineImages_PreservesDataUriImages()
    {
        var base64 = Convert.ToBase64String(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        var html =
            $"<html><head><style>p {{ color: red; }}</style></head>"
            + $"<body><p>Hello</p><img src=\"data:image/png;base64,{base64}\"></body></html>";

        var inlined = EmailMessageBuilder.InlineCss(html);

        // CSS inlining should not alter the img data URI
        Assert.Contains($"data:image/png;base64,{base64}", inlined);

        var result = EmailMessageBuilder.ExtractInlineImages(inlined);

        Assert.Single(result.Images);
        Assert.Contains("cid:", result.ProcessedHtml);
        Assert.DoesNotContain("data:image", result.ProcessedHtml);
        // Verify the paragraph got its style inlined
        Assert.Contains("color: red", result.ProcessedHtml);
    }
}
