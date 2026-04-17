using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Web.UnitTests;

public sealed class LegacyWixRedirectTests
{
    private static async Task<HttpContext> InvokeMiddleware(string path, string queryString = "")
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        if (!string.IsNullOrEmpty(queryString))
            context.Request.QueryString = new QueryString(queryString);

        var services = new ServiceCollection().BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        Startup.MapLegacyWixRedirects(app);
        app.Run(ctx =>
        {
            ctx.Items["NextCalled"] = true;
            return Task.CompletedTask;
        });

        var pipeline = app.Build();
        await pipeline(context);

        return context;
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
        var context = await InvokeMiddleware(path);

        Assert.Equal(StatusCodes.Status301MovedPermanently, context.Response.StatusCode);
        Assert.Equal("/about", context.Response.Headers.Location.ToString());
    }

    [Fact]
    public async Task Redirect_preserves_query_string()
    {
        var context = await InvokeMiddleware("/faq", "?utm_source=google");

        Assert.Equal(StatusCodes.Status301MovedPermanently, context.Response.StatusCode);
        Assert.Equal("/about?utm_source=google", context.Response.Headers.Location.ToString());
    }

    [Theory]
    [InlineData("/about")]
    [InlineData("/")]
    [InlineData("/events")]
    [InlineData("/faq/extra")]
    [InlineData("/about-us-more")]
    public async Task Non_legacy_paths_pass_through(string path)
    {
        var context = await InvokeMiddleware(path);

        Assert.NotEqual(StatusCodes.Status301MovedPermanently, context.Response.StatusCode);
    }
}
