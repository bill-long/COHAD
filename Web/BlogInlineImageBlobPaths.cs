using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Web;

/// <summary>
/// Finds blob storage paths for images uploaded via <c>/api/blog/upload-image</c> (Markdown image syntax).
/// </summary>
internal static class BlogInlineImageBlobPaths
{
    private static readonly Regex MarkdownImageRegex = new(
        @"!\[[^\]]*\]\(([^)]+)\)",
        RegexOptions.Compiled);

    /// <summary>
    /// Returns distinct <c>blog/images/...</c> paths referenced in Markdown (suitable for <see cref="Services.IDocumentFileStore.DeleteAsync"/>).
    /// </summary>
    public static IReadOnlyList<string> ExtractFromMarkdown(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return Array.Empty<string>();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();

        foreach (Match m in MarkdownImageRegex.Matches(markdown))
        {
            var url = m.Groups[1].Value.Trim().Trim('"', '\'');
            var cut = url.IndexOfAny(new[] { '?', '#', ' ' });
            if (cut >= 0)
            {
                url = url[..cut];
            }

            var blob = ToBlogImagesBlobPath(url);
            if (blob != null && seen.Add(blob))
            {
                list.Add(blob);
            }
        }

        return list;
    }

    private static string ToBlogImagesBlobPath(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        const StringComparison o = StringComparison.OrdinalIgnoreCase;
        const string apiPrefix = "/api/blog/images/";
        const string blobPrefix = "blog/images/";

        if (url.StartsWith(apiPrefix, o))
        {
            return blobPrefix + DecodeBlobRelativePath(url[apiPrefix.Length..]);
        }

        if (url.StartsWith(blobPrefix, o))
        {
            var relative = url[blobPrefix.Length..];
            return blobPrefix + DecodeBlobRelativePath(relative);
        }

        var apiIdx = url.IndexOf(apiPrefix, o);
        if (apiIdx >= 0)
        {
            return blobPrefix + DecodeBlobRelativePath(url[(apiIdx + apiPrefix.Length)..]);
        }

        var blobIdx = url.IndexOf(blobPrefix, o);
        if (blobIdx >= 0)
        {
            var relative = url[(blobIdx + blobPrefix.Length)..];
            return blobPrefix + DecodeBlobRelativePath(relative);
        }

        return null;
    }

    private static string DecodeBlobRelativePath(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            return relativePath;
        }

        try
        {
            return Uri.UnescapeDataString(relativePath);
        }
        catch (UriFormatException)
        {
            return relativePath;
        }
    }
}
