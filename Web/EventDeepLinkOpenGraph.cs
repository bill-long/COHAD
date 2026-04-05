using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Web.Models;
using Web.Services;
using Web.Services.Repositories;

namespace Web;

/// <summary>
/// Injects Open Graph (and Twitter Card) meta tags into the SPA shell HTML for <c>/events/{segment}</c> so
/// crawlers (e.g. Facebook) receive title, description, and image in the first response.
/// After deployment, validate with the Facebook Sharing Debugger: https://developers.facebook.com/tools/debug/
/// </summary>
/// <remarks>
/// The route is registered only when <c>ClientApp/dist/cohad-app/index.html</c> exists (production/publish build).
/// In Development with only <c>ng serve</c> and no dist output, <c>/events/…</c> is handled by the SPA proxy so deep-link refresh still loads the app; Open Graph tags are not injected until a built index is present.
/// </remarks>
public static class EventDeepLinkOpenGraphEndpointExtensions
{
    public static IEndpointRouteBuilder MapEventDeepLinkOpenGraph(
        this IEndpointRouteBuilder endpoints,
        IWebHostEnvironment env
    )
    {
        var distIndex = Path.Combine(env.ContentRootPath, "ClientApp", "dist", "cohad-app", "index.html");
        if (!File.Exists(distIndex))
        {
            return endpoints;
        }

        endpoints.MapGet(
            "/events/{segment}",
            (HttpContext http, string segment) => WriteEventPageAsync(http, env, segment)
        );
        return endpoints;
    }

    private static async Task WriteEventPageAsync(HttpContext context, IWebHostEnvironment env, string segment)
    {
        CommunityEvent ev;
        using (var scope = context.RequestServices.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<ICommunityEventRepository>();
            ev = await repo.GetByRouteSegmentAsync(segment);
        }

        // Redirect old slug aliases to the canonical URL so crawlers and bookmarks update.
        if (
            ev != null
            && !string.IsNullOrWhiteSpace(ev.PublicSlug)
            && !string.Equals(segment, ev.PublicSlug, StringComparison.OrdinalIgnoreCase)
        )
        {
            var canonicalPath = $"/events/{Uri.EscapeDataString(ev.PublicSlug)}";
            var queryString = context.Request.QueryString.HasValue ? context.Request.QueryString.Value : string.Empty;
            context.Response.StatusCode = StatusCodes.Status301MovedPermanently;
            context.Response.Headers["Location"] = canonicalPath + queryString;
            return;
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

        if (ev != null)
        {
            var canonicalSegment = Uri.EscapeDataString(EventUrlSlug.ResolveUrlSegment(ev));
            var canonical = $"{baseUrl.TrimEnd('/')}/events/{canonicalSegment}";
            var ogTitle = string.IsNullOrWhiteSpace(ev.Title) ? "Event" : ev.Title.Trim();
            var ogDescription = BuildOgDescription(ev.Description ?? string.Empty);
            var imageUrl = ResolveOgImageUrl(baseUrl, ev, out var hasOgThumb);

            var metaBlock = BuildMetaBlock(
                HtmlEncoder.Default.Encode(ogTitle),
                HtmlEncoder.Default.Encode(ogDescription),
                HtmlEncoder.Default.Encode(canonical),
                HtmlEncoder.Default.Encode(imageUrl),
                hasOgThumb
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

    /// <summary>
    /// Avoids re-reading <c>index.html</c> from disk on every crawler hit; invalidates when the file&apos;s last write time changes.
    /// </summary>
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

    /// <summary>
    /// Public base URL for canonical and image links. Uses <see cref="HttpRequest.Scheme"/> and <see cref="HttpRequest.Host"/>
    /// (forwarded proto is applied by <c>UseForwardedHeaders</c>). Raw <c>X-Forwarded-Host</c> is not read here to avoid
    /// client-spoofed canonical URLs unless the host is configured with known proxies and forwarded host processing.
    /// </summary>
    private static string GetPublicBaseUrl(HttpRequest request)
    {
        return $"{request.Scheme}://{request.Host.Value}";
    }

    /// <summary>
    /// Promo image when content type is image/*; otherwise a static app asset that exists in the Angular build.
    /// </summary>
    private static string ResolveOgImageUrl(string baseUrl, CommunityEvent ev, out bool hasOgThumb)
    {
        var root = baseUrl.TrimEnd('/');
        var segment = Uri.EscapeDataString(EventUrlSlug.ResolveUrlSegment(ev));
        if (
            !string.IsNullOrWhiteSpace(ev.PromoMediaBlobPath)
            && !string.IsNullOrWhiteSpace(ev.PromoMediaContentType)
            && ev.PromoMediaContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
        )
        {
            hasOgThumb = true;
            return $"{root}/api/events/{segment}/promo/og-thumb";
        }

        hasOgThumb = false;
        return $"{root}/assets/trees1.jpg";
    }

    private static string BuildOgDescription(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return "Community event — Canyon Oaks Homeowners Association";
        }

        var collapsed = Regex.Replace(description.Trim(), @"\s+", " ");
        const int maxLen = 300;
        if (collapsed.Length <= maxLen)
        {
            return collapsed;
        }

        return collapsed[..(maxLen - 1)].TrimEnd() + "…";
    }

    private static string BuildMetaBlock(
        string encodedTitle,
        string encodedDescription,
        string encodedCanonical,
        string encodedImageUrl,
        bool includeImageDimensions
    )
    {
        var sb = new StringBuilder();
        sb.AppendLine($"    <meta property=\"og:type\" content=\"website\" />");
        sb.AppendLine($"    <meta property=\"og:site_name\" content=\"COHAD\" />");
        sb.AppendLine($"    <meta property=\"og:title\" content=\"{encodedTitle}\" />");
        sb.AppendLine($"    <meta property=\"og:description\" content=\"{encodedDescription}\" />");
        sb.AppendLine($"    <meta property=\"og:url\" content=\"{encodedCanonical}\" />");
        sb.AppendLine($"    <meta property=\"og:image\" content=\"{encodedImageUrl}\" />");
        if (includeImageDimensions)
        {
            sb.AppendLine(
                $"    <meta property=\"og:image:width\" content=\"{SkiaSharpOgThumbnailService.TargetWidth}\" />"
            );
            sb.AppendLine(
                $"    <meta property=\"og:image:height\" content=\"{SkiaSharpOgThumbnailService.TargetHeight}\" />"
            );
        }

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
