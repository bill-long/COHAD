using Web;

namespace Web.UnitTests;

public sealed class BlogInlineImageBlobPathsTests
{
    [Fact]
    public void ExtractFromMarkdown_returns_distinct_blog_images_blob_paths()
    {
        var md = """
            ![a](/api/blog/images/11111111-1111-1111-1111-111111111111/pic.jpg)
            ![b](blog/images/22222222-2222-2222-2222-222222222222/x.png)
            ![dup](/api/blog/images/11111111-1111-1111-1111-111111111111/pic.jpg)
            """;

        var paths = BlogInlineImageBlobPaths.ExtractFromMarkdown(md);

        Assert.Equal(2, paths.Count);
        Assert.Contains("blog/images/11111111-1111-1111-1111-111111111111/pic.jpg", paths);
        Assert.Contains("blog/images/22222222-2222-2222-2222-222222222222/x.png", paths);
    }

    [Fact]
    public void ExtractFromMarkdown_strips_query_from_url()
    {
        var md = "![](/api/blog/images/guid/f.webp?x=1#frag)";
        var paths = BlogInlineImageBlobPaths.ExtractFromMarkdown(md);
        Assert.Single(paths);
        Assert.Equal("blog/images/guid/f.webp", paths[0]);
    }

    [Fact]
    public void ExtractFromMarkdown_handles_absolute_site_url()
    {
        var md = "![](https://example.com/api/blog/images/guid/f.jpg)";
        var paths = BlogInlineImageBlobPaths.ExtractFromMarkdown(md);
        Assert.Single(paths);
        Assert.Equal("blog/images/guid/f.jpg", paths[0]);
    }

    [Fact]
    public void ExtractFromMarkdown_decodes_percent_encoded_file_name()
    {
        var md = "![](/api/blog/images/guid/my%20photo%23final.jpg)";
        var paths = BlogInlineImageBlobPaths.ExtractFromMarkdown(md);
        Assert.Single(paths);
        Assert.Equal("blog/images/guid/my photo#final.jpg", paths[0]);
    }
}
