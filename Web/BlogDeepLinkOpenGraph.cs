using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Web.Models;
using Web.Services.Repositories;

namespace Web;

/// <summary>
/// Injects Open Graph (and Twitter Card) meta tags into the SPA shell HTML for <c>/news/{segment}</c> so
/// crawlers (e.g. Facebook) receive title, description, and image in the first response.
/// </summary>
public static class BlogDeepLinkOpenGraphEndpointExtensions
{
    public static IEndpointRouteBuilder MapBlogDeepLinkOpenGraph(
        this IEndpointRouteBuilder endpoints,
        IWebHostEnvironment env
    )
    {
        var distIndex = Path.Combine(env.ContentRootPath, "ClientApp", "dist", "cohad-app", "index.html");
        if (!File.Exists(distIndex))
        {
            return endpoints;
        }

        endpoints.MapMethods(
            "/news/{segment}",
            ["GET", "HEAD"],
            (HttpContext http, string segment) => WriteBlogPageAsync(http, env, segment)
        );
        return endpoints;
    }

    private static async Task WriteBlogPageAsync(HttpContext context, IWebHostEnvironment env, string segment)
    {
        BlogPost post;
        using (var scope = context.RequestServices.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IBlogPostRepository>();
            post = await repo.GetByRouteSegmentAsync(segment);
        }

        var indexPath = ResolveIndexHtmlPath(env);
        if (string.IsNullOrEmpty(indexPath) || !File.Exists(indexPath))
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsync("SPA index.html not found on server.", Encoding.UTF8);
            return;
        }

        var cache = context.RequestServices.GetRequiredService<IMemoryCache>();
        var html = await ReadSpaIndexHtmlCachedAsync(indexPath, cache);
        var baseUrl = GetPublicBaseUrl(context.Request);

        if (post != null)
        {
            var canonicalSegment = Uri.EscapeDataString(BlogUrlSlug.ResolveUrlSegment(post));
            var canonical = $"{baseUrl.TrimEnd('/')}/news/{canonicalSegment}";
            var ogTitle = string.IsNullOrWhiteSpace(post.Title) ? "Blog Post" : post.Title.Trim();
            var ogDescription = BuildOgDescription(post.Excerpt ?? post.Content ?? string.Empty);
            var imageUrl = ResolveOgImageUrl(baseUrl, post);

            var metaBlock = BuildMetaBlock(
                HtmlEncoder.Default.Encode(ogTitle),
                HtmlEncoder.Default.Encode(ogDescription),
                HtmlEncoder.Default.Encode(canonical),
                HtmlEncoder.Default.Encode(imageUrl)
            );

            html = InsertAfterOpenHead(html, metaBlock);
            html = ReplaceDocumentTitle(html, WebUtility.HtmlEncode(ogTitle) + " · COHAD");
        }

        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(html, Encoding.UTF8);
    }

    private static string ResolveIndexHtmlPath(IWebHostEnvironment env)
    {
        var dist = Path.Combine(env.ContentRootPath, "ClientApp", "dist", "cohad-app", "index.html");
        return File.Exists(dist) ? dist : string.Empty;
    }

    private sealed record SpaIndexHtmlCacheEntry(long LastWriteUtcTicks, string Html);

    private static async Task<string> ReadSpaIndexHtmlCachedAsync(string indexPath, IMemoryCache cache)
    {
        long ticks;
        try
        {
            ticks = new FileInfo(indexPath).LastWriteTimeUtc.Ticks;
        }
        catch (IOException)
        {
            return await File.ReadAllTextAsync(indexPath);
        }
        catch (UnauthorizedAccessException)
        {
            return await File.ReadAllTextAsync(indexPath);
        }
        catch (ArgumentException)
        {
            return await File.ReadAllTextAsync(indexPath);
        }

        var cacheKey = $"SpaIndexHtml:{indexPath}";
        if (cache.TryGetValue(cacheKey, out SpaIndexHtmlCacheEntry entry) && entry.LastWriteUtcTicks == ticks)
        {
            return entry.Html;
        }

        var html = await File.ReadAllTextAsync(indexPath);
        cache.Set(
            cacheKey,
            new SpaIndexHtmlCacheEntry(ticks, html),
            new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromHours(12) }
        );
        return html;
    }

    private static string GetPublicBaseUrl(HttpRequest request)
    {
        return $"{request.Scheme}://{request.Host.Value}";
    }

    private static string ResolveOgImageUrl(string baseUrl, BlogPost post)
    {
        var root = baseUrl.TrimEnd('/');
        var segment = Uri.EscapeDataString(BlogUrlSlug.ResolveUrlSegment(post));
        if (
            !string.IsNullOrWhiteSpace(post.FeaturedImageBlobPath)
            && !string.IsNullOrWhiteSpace(post.FeaturedImageContentType)
            && post.FeaturedImageContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
        )
        {
            return $"{root}/api/blog/{segment}/image";
        }

        return $"{root}/assets/trees1.jpg";
    }

    private static string BuildOgDescription(string rawText)
    {
        var result = MarkdownPlainText.ToPlainText(rawText);
        return string.IsNullOrEmpty(result) ? "Blog post — Canyon Oaks Homeowners Association" : result;
    }

    private static string BuildMetaBlock(
        string encodedTitle,
        string encodedDescription,
        string encodedCanonical,
        string encodedImageUrl
    )
    {
        var sb = new StringBuilder();
        sb.AppendLine($"    <meta property=\"og:type\" content=\"article\" />");
        sb.AppendLine($"    <meta property=\"og:site_name\" content=\"COHAD\" />");
        sb.AppendLine($"    <meta property=\"og:title\" content=\"{encodedTitle}\" />");
        sb.AppendLine($"    <meta property=\"og:description\" content=\"{encodedDescription}\" />");
        sb.AppendLine($"    <meta property=\"og:url\" content=\"{encodedCanonical}\" />");
        sb.AppendLine($"    <meta property=\"og:image\" content=\"{encodedImageUrl}\" />");
        sb.AppendLine($"    <meta name=\"twitter:card\" content=\"summary_large_image\" />");
        sb.AppendLine($"    <meta name=\"twitter:title\" content=\"{encodedTitle}\" />");
        sb.AppendLine($"    <meta name=\"twitter:description\" content=\"{encodedDescription}\" />");
        sb.AppendLine($"    <meta name=\"twitter:image\" content=\"{encodedImageUrl}\" />");
        return sb.ToString();
    }

    private static string InsertAfterOpenHead(string html, string insertion)
    {
        const string headOpen = "<head>";
        var idx = html.IndexOf(headOpen, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return html;
        }

        var insertAt = idx + headOpen.Length;
        return html.Insert(insertAt, "\n" + insertion);
    }

    private static string ReplaceDocumentTitle(string html, string encodedTitleAndSuffix)
    {
        const string oldTitle = "<title>COHAD</title>";
        var replacement = $"<title>{encodedTitleAndSuffix}</title>";
        if (html.Contains(oldTitle, StringComparison.Ordinal))
        {
            return html.Replace(oldTitle, replacement, StringComparison.Ordinal);
        }

        return html;
    }
}
