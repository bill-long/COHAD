using System.Net;
using System.Text.RegularExpressions;

namespace Web;

/// <summary>
/// Strips Markdown syntax and HTML, collapses whitespace, and truncates to produce a plain-text summary.
/// </summary>
internal static class MarkdownPlainText
{
    internal static string ToPlainText(string markdown, int maxLength = 300)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var text = markdown;
        // Remove images: ![alt](url)
        text = Regex.Replace(text, @"!\[[^\]]*\]\([^)]*\)", " ");
        // Remove links but keep text: [text](url) → text
        text = Regex.Replace(text, @"\[([^\]]*)\]\([^)]*\)", "$1");
        // Remove heading markers
        text = Regex.Replace(text, @"^#{1,6}\s+", "", RegexOptions.Multiline);
        // Remove bold/italic markers
        text = Regex.Replace(text, @"(\*{1,3}|_{1,3})(.+?)\1", "$2");
        // Remove strikethrough
        text = Regex.Replace(text, @"~~(.+?)~~", "$1");
        // Remove inline code
        text = Regex.Replace(text, @"`([^`]+)`", "$1");
        // Remove code fences
        text = Regex.Replace(text, @"```[\s\S]*?```", " ");
        // Remove blockquote markers
        text = Regex.Replace(text, @"^>\s?", "", RegexOptions.Multiline);
        // Remove horizontal rules
        text = Regex.Replace(text, @"^[-*_]{3,}\s*$", "", RegexOptions.Multiline);
        // Remove list markers
        text = Regex.Replace(text, @"^[\s]*[-*+]\s+", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^[\s]*\d+\.\s+", "", RegexOptions.Multiline);
        // Remove any remaining HTML tags
        text = Regex.Replace(text, @"<[^>]+>", " ");
        text = WebUtility.HtmlDecode(text);
        // Collapse whitespace
        text = Regex.Replace(text.Trim(), @"\s+", " ");

        if (text.Length <= maxLength)
        {
            return text;
        }

        return text[..(maxLength - 1)].TrimEnd() + "\u2026";
    }
}
