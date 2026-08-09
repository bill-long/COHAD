using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using MimeKit;
using MimeKit.Utils;

namespace Web.Services
{
    internal static class EmailMessageBuilder
    {
        /// <summary>
        /// Moves CSS from &lt;style&gt; blocks into inline style attributes so that
        /// email clients (Gmail, Outlook, etc.) render styles correctly.
        /// If no &lt;style&gt; blocks are present or inlining fails, the original HTML is returned.
        /// </summary>
        public static string InlineCss(string? html)
        {
            if (string.IsNullOrWhiteSpace(html))
                return html ?? string.Empty;

            if (html.IndexOf("<style", StringComparison.OrdinalIgnoreCase) < 0)
                return html;

            try
            {
                var result = PreMailer.Net.PreMailer.MoveCssInline(
                    html,
                    removeStyleElements: true,
                    ignoreElements: "#ignore"
                );
                return result.Html;
            }
            catch
            {
                // Fall back to original HTML so email delivery is not blocked
                return html;
            }
        }

        private static readonly Regex DataUriImgRegex = new(
            @"(<img\b[^>]*?)\bsrc\s*=\s*""data:image/([^;]+);base64,([^""]+)""([^>]*?>)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled
        );

        private const int MaxBase64Length = 10 * 1024 * 1024; // ~7.5 MB decoded

        private static readonly Dictionary<string, string> MediaSubtypeToExtension = new(
            StringComparer.OrdinalIgnoreCase
        )
        {
            ["svg+xml"] = "svg",
            ["jpeg"] = "jpg",
        };

        public static ExtractedImages ExtractInlineImages(string htmlBody)
        {
            var images = new List<InlineImage>();
            int imageCount = 0;

            var processed = DataUriImgRegex.Replace(
                htmlBody,
                match =>
                {
                    var mediaSubtype = match.Groups[2].Value;
                    var base64 = match.Groups[3].Value;

                    if (base64.Length > MaxBase64Length)
                        return match.Value; // leave oversized images as-is

                    byte[] imageBytes;
                    try
                    {
                        imageBytes = Convert.FromBase64String(base64);
                    }
                    catch (FormatException)
                    {
                        return match.Value; // leave malformed data URIs as-is
                    }

                    var extension = MediaSubtypeToExtension.TryGetValue(mediaSubtype, out var mapped)
                        ? mapped
                        : mediaSubtype;
                    var contentId = MimeUtils.GenerateMessageId();
                    images.Add(
                        new InlineImage
                        {
                            FileName = $"image{imageCount++}.{extension}",
                            ContentId = contentId,
                            Data = imageBytes,
                        }
                    );
                    var prefix = match.Groups[1].Value;
                    var suffix = match.Groups[4].Value;
                    return $"{prefix}src=\"cid:{contentId}\"{suffix}";
                }
            );

            return new ExtractedImages { ProcessedHtml = processed, Images = images };
        }

        public static MimeEntity BuildBodyWithImages(string html, List<InlineImage> images, List<(string fileName, byte[] data, string contentType)>? attachments = null)
        {
            var bodyBuilder = new BodyBuilder();
            foreach (var img in images)
            {
                var resource = bodyBuilder.LinkedResources.Add(img.FileName, img.Data);
                resource.ContentId = img.ContentId;
            }
            bodyBuilder.HtmlBody = html;
            if (attachments != null)
            {
                foreach (var (fileName, data, contentType) in attachments)
                {
                    ContentType ct;
                    try
                    {
                        ct = !string.IsNullOrWhiteSpace(contentType)
                            ? ContentType.Parse(contentType)
                            : new ContentType("application", "octet-stream");
                    }
                    catch
                    {
                        ct = new ContentType("application", "octet-stream");
                    }
                    var safeFileName = string.IsNullOrWhiteSpace(fileName) ? "attachment" : fileName;
                    bodyBuilder.Attachments.Add(safeFileName, data, ct);
                }
            }
            return bodyBuilder.ToMessageBody();
        }

        /// <summary>
        /// Builds the unsubscribe footer, or an empty string when there is nothing to link to.
        /// <para>
        /// The link is the short <c>/u/{id}</c> form, around 35 characters against the ~190 of the
        /// <c>?token=</c> form it replaces. Length is the reason this changed: a long URL is exposed
        /// to quoted-printable soft line breaks at 76 characters and to gateway rewriting, both
        /// documented causes of the truncated-credential failures behind
        /// docs/email-suppression-and-unsubscribe.md. Keep it short.
        /// </para>
        /// </summary>
        public static string BuildUnsubscribeFooter(
            string appBaseUrl,
            string categoryDisplayName,
            string unsubscribeLinkId
        )
        {
            if (string.IsNullOrEmpty(appBaseUrl) || string.IsNullOrEmpty(unsubscribeLinkId))
                return "";

            var prefsUrl = $"{appBaseUrl}/u/{Uri.EscapeDataString(unsubscribeLinkId)}";

            return "\n<hr style=\"margin-top:32px;border:none;border-top:1px solid #ddd\">"
                + "<p style=\"font-size:12px;color:#888;font-family:sans-serif;\">"
                + $"You received this email because your address is subscribed to COHAD {categoryDisplayName} updates. "
                + $"<a href=\"{prefsUrl}\" style=\"color:#1a73e8;\">Manage your email preferences</a>"
                + "</p>";
        }
    }

    internal class ExtractedImages
    {
        public string ProcessedHtml { get; set; }
        public List<InlineImage> Images { get; set; }
    }

    internal class InlineImage
    {
        public string FileName { get; set; }
        public string ContentId { get; set; }
        public byte[] Data { get; set; }
    }
}
