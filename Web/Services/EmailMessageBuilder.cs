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
            @"<img\b[^>]*?\bsrc\s*=\s*""data:image/([^;]+);base64,([^""]+)""[^>]*?>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled
        );

        public static ExtractedImages ExtractInlineImages(string htmlBody)
        {
            var images = new List<InlineImage>();
            int imageCount = 0;

            var processed = DataUriImgRegex.Replace(
                htmlBody,
                match =>
                {
                    var imageExtension = match.Groups[1].Value;
                    var base64 = match.Groups[2].Value;
                    var imageBytes = Convert.FromBase64String(base64);

                    var contentId = MimeUtils.GenerateMessageId();
                    images.Add(
                        new InlineImage
                        {
                            FileName = $"image{imageCount++}.{imageExtension}",
                            ContentId = contentId,
                            Data = imageBytes,
                        }
                    );
                    return $"<img src=\"cid:{contentId}\">";
                }
            );

            return new ExtractedImages { ProcessedHtml = processed, Images = images };
        }

        public static MimeEntity BuildBodyWithImages(string html, List<InlineImage> images)
        {
            var bodyBuilder = new BodyBuilder();
            foreach (var img in images)
            {
                var resource = bodyBuilder.LinkedResources.Add(img.FileName, img.Data);
                resource.ContentId = img.ContentId;
            }
            bodyBuilder.HtmlBody = html;
            return bodyBuilder.ToMessageBody();
        }

        public static string BuildUnsubscribeFooter(string appBaseUrl, string categoryDisplayName, string token)
        {
            if (string.IsNullOrEmpty(appBaseUrl) || string.IsNullOrEmpty(token))
                return "";

            var prefsUrl = $"{appBaseUrl}/email-preferences?token={Uri.EscapeDataString(token)}";

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
