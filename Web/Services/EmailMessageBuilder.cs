using System;
using System.Collections.Generic;
using System.Text;
using MimeKit;
using MimeKit.Utils;

namespace Web.Services
{
    internal static class EmailMessageBuilder
    {
        public static ExtractedImages ExtractInlineImages(string htmlBody)
        {
            var imageStart = "<img src=\"data:";
            var imageEnd = "\">";
            var images = new List<InlineImage>();
            var sb = new StringBuilder();

            int imageCount = 0;
            int position = 0;
            while (position < htmlBody.Length)
            {
                var nextImageStart = htmlBody.IndexOf(imageStart, position);
                if (nextImageStart < 0)
                {
                    sb.Append(htmlBody.AsSpan(position));
                    break;
                }

                sb.Append(htmlBody.AsSpan(position, nextImageStart - position));

                var imageTypeStartPos = nextImageStart + imageStart.Length;
                var imageTypeEndPos = htmlBody.IndexOf(';', imageTypeStartPos);
                var imageType = htmlBody[imageTypeStartPos..imageTypeEndPos];
                var imageExtension = imageType[(imageType.IndexOf('/') + 1)..];

                var encodingStartPos = imageTypeEndPos + 1;
                var encodingEndPos = htmlBody.IndexOf(',', encodingStartPos);
                var encoding = htmlBody[encodingStartPos..encodingEndPos];
                if (encoding != "base64")
                    throw new InvalidOperationException($"Unsupported image encoding: {encoding}");

                var base64Start = encodingEndPos + 1;
                var base64End = htmlBody.IndexOf(imageEnd, base64Start);
                var base64 = htmlBody[base64Start..base64End];
                var imageBytes = Convert.FromBase64String(base64);

                var contentId = MimeUtils.GenerateMessageId();
                images.Add(new InlineImage
                {
                    FileName = $"image{imageCount++}.{imageExtension}",
                    ContentId = contentId,
                    Data = imageBytes
                });
                sb.Append($"<img src=\"cid:{contentId}\">");

                position = base64End + imageEnd.Length;
            }

            return new ExtractedImages { ProcessedHtml = sb.ToString(), Images = images };
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

            return "\n<hr style=\"margin-top:32px;border:none;border-top:1px solid #ddd\">" +
                   "<p style=\"font-size:12px;color:#888;font-family:sans-serif;\">" +
                   $"You received this email because your address is subscribed to COHAD {categoryDisplayName} updates. " +
                   $"<a href=\"{prefsUrl}\" style=\"color:#1a73e8;\">Manage your email preferences</a>" +
                   "</p>";
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
