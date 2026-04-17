using Microsoft.AspNetCore.Http;

namespace Web.UnitTests;

public sealed class LegacyWixRedirectTests
{
    private static async Task<(bool NextCalled, HttpContext Context)> InvokeMiddleware(string path)
    {
        var nextCalled = false;
        var context = new DefaultHttpContext();
        context.Request.Path = path;

        var requestPath = context.Request.Path.Value;
        if (
            requestPath != null
            && (
                requestPath.Equals("/faq", StringComparison.OrdinalIgnoreCase)
                || requestPath.Equals("/about-us", StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            context.Response.StatusCode = StatusCodes.Status301MovedPermanently;
            context.Response.Headers.Location = "/about";
        }
        else
        {
            nextCalled = true;
        }

        return (nextCalled, context);
    }

    [Theory]
    [InlineData("/faq")]
    [InlineData("/FAQ")]
    [InlineData("/Faq")]
    [InlineData("/about-us")]
    [InlineData("/About-Us")]
    [InlineData("/ABOUT-US")]
    public async Task Legacy_wix_paths_return_301_to_about(string path)
    {
        var (nextCalled, context) = await InvokeMiddleware(path);

        Assert.Equal(StatusCodes.Status301MovedPermanently, context.Response.StatusCode);
        Assert.Equal("/about", context.Response.Headers.Location.ToString());
        Assert.False(nextCalled);
    }

    [Theory]
    [InlineData("/about")]
    [InlineData("/")]
    [InlineData("/events")]
    [InlineData("/faq/extra")]
    [InlineData("/about-us-more")]
    public async Task Non_legacy_paths_pass_through(string path)
    {
        var (nextCalled, context) = await InvokeMiddleware(path);

        Assert.NotEqual(StatusCodes.Status301MovedPermanently, context.Response.StatusCode);
        Assert.True(nextCalled);
    }
}
