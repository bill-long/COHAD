using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Configuration;
using MimeKit;
using MimeKit.Utils;
using Web.Configuration;
using Web.Models;
using Web.Services.Repositories;
using Web.UpdateModels;

namespace Web.Services
{
    public interface IEmailService
    {
        /// <summary>
        /// Sends a committee email to all recipients matching the filter, with per-recipient
        /// unsubscribe headers and footer.
        /// </summary>
        /// <param name="category">Unsubscribe category key (e.g. "board", "welcome").</param>
        Task SendEmail(string fromEmail, string fromDisplay, EmailInfo emailInfo,
            Func<EmailAddress, bool> recipientFilter,
            string category, ClaimsPrincipal user);

        /// <summary>
        /// Sends an email to an explicit list of addresses (no unsubscribe headers).
        /// Used for transactional/system emails.
        /// </summary>
        Task SendEmail(string fromEmail, string fromDisplay, EmailInfo emailInfo,
            List<string> toList, ClaimsPrincipal user);
    }

    public class EmailService : IEmailService
    {
        private readonly IUserRepository _userRepository;
        private readonly IHomeRepository _homeRepository;
        private readonly IUnsubscribeTokenService _tokenService;
        private readonly SmtpOptions _options;
        private readonly string _appBaseUrl;

        public EmailService(
            IUserRepository userRepository,
            IHomeRepository homeRepository,
            IUnsubscribeTokenService tokenService,
            IConfiguration config)
        {
            _userRepository = userRepository;
            _homeRepository = homeRepository;
            _tokenService = tokenService;
            _options = new SmtpOptions
            {
                SmtpHost = config["SmtpHost"],
                SmtpUser = config["SmtpUser"],
                SmtpPassword = config["SmtpPassword"]
            };
            _appBaseUrl = (config["AppBaseUrl"] ?? "").TrimEnd('/');
        }

        public async Task SendEmail(string fromEmail, string fromDisplay, EmailInfo emailInfo,
            Func<EmailAddress, bool> recipientFilter,
            string category, ClaimsPrincipal user)
        {
            var recipients = await GetAllEmailsMatchingFilter(recipientFilter);
            await SendPerRecipientEmails(fromEmail, fromDisplay, emailInfo, recipients,
                category, user);
        }

        public async Task SendEmail(string fromEmail, string fromDisplay, EmailInfo emailInfo,
            List<string> toList, ClaimsPrincipal user)
        {
            await SendDirectEmail(fromEmail, fromDisplay, emailInfo, toList, user);
        }

        /// <summary>
        /// Sends individual emails per recipient with unsubscribe headers and footer.
        /// </summary>
        private async Task SendPerRecipientEmails(string fromEmail, string fromDisplay,
            EmailInfo emailInfo, List<EmailRecipient> recipients,
            string category, ClaimsPrincipal user)
        {
            var subject = emailInfo.Subject;
            List<EmailRecipient> recipientList = recipients;

            if (emailInfo.IsTestEmail)
            {
                var apiUser = await _userRepository.GetByUniqueIdAsync(
                    Models.User.GetUniqueIdFromClaims(user.Claims));
                recipientList = new List<EmailRecipient>
                {
                    new EmailRecipient { Email = apiUser.Emails, HomeId = Guid.Empty }
                };
                subject = $"Test: {subject}";
            }

            if (recipientList.Count == 0)
                return;

            // Extract images once (shared across all per-recipient messages)
            var imageData = ExtractInlineImages(emailInfo.HtmlBody);
            var categoryDisplayName = Controllers.UnsubscribeController.CategoryDisplayNames
                .TryGetValue(category ?? "", out var name) ? name : category;

            var protocolLog = new MemoryStream();
            var logger = new ProtocolLogger(protocolLog);
            try
            {
                using var smtpClient = new SmtpClient(logger);
                await smtpClient.ConnectAsync(_options.SmtpHost, 587,
                    MailKit.Security.SecureSocketOptions.StartTls);
                await smtpClient.AuthenticateAsync(_options.SmtpUser, _options.SmtpPassword);

#if DEBUG
                // In DEBUG, send a single representative message to test addresses
                // instead of one per recipient, to avoid spamming debug inboxes.
                var debugRecipient = recipientList[0];
                var debugToken = (debugRecipient.HomeId != Guid.Empty && !string.IsNullOrEmpty(_appBaseUrl))
                    ? _tokenService.GenerateToken(debugRecipient.HomeId, debugRecipient.Email)
                    : null;
                var debugFooter = BuildUnsubscribeFooter(category, categoryDisplayName, debugToken);
                var debugMessage = new MimeMessage();
                debugMessage.From.Add(new MailboxAddress(fromDisplay, fromEmail));
                debugMessage.Subject = $"[DEBUG {recipientList.Count} recipients] {subject}";
                debugMessage.ReplyTo.Add(new MailboxAddress(fromDisplay, fromEmail));
                debugMessage.Bcc.Add(new MailboxAddress(null, "bill@cohad.org"));
                debugMessage.Bcc.Add(new MailboxAddress(null, "bilongtest@gmail.com"));
                debugMessage.To.Add(new GroupAddress("Private Recipients"));
                debugMessage.Body = BuildBodyWithImages(imageData.ProcessedHtml + debugFooter, imageData.Images);
                if (debugToken != null && !string.IsNullOrEmpty(_appBaseUrl))
                {
                    var unsubUrl = $"{_appBaseUrl}/api/email/unsubscribe/{category}?token={Uri.EscapeDataString(debugToken)}";
                    debugMessage.Headers.Add("List-Unsubscribe", $"<{unsubUrl}>");
                    debugMessage.Headers.Add("List-Unsubscribe-Post", "List-Unsubscribe=One-Click");
                }
                await smtpClient.SendAsync(debugMessage);
#else
                foreach (var recipient in recipientList)
                {
                    // Reset protocol log between messages to bound memory usage
                    protocolLog.SetLength(0);

                    var token = (recipient.HomeId != Guid.Empty && !string.IsNullOrEmpty(_appBaseUrl))
                        ? _tokenService.GenerateToken(recipient.HomeId, recipient.Email)
                        : null;

                    var footer = BuildUnsubscribeFooter(category, categoryDisplayName, token);
                    var htmlWithFooter = imageData.ProcessedHtml + footer;

                    var message = new MimeMessage();
                    message.From.Add(new MailboxAddress(fromDisplay, fromEmail));
                    message.Subject = subject;
                    message.ReplyTo.Add(new MailboxAddress(fromDisplay, fromEmail));
                    message.To.Add(new MailboxAddress("", recipient.Email));

                    message.Body = BuildBodyWithImages(htmlWithFooter, imageData.Images);

                    if (token != null && !string.IsNullOrEmpty(_appBaseUrl))
                    {
                        var unsubUrl = $"{_appBaseUrl}/api/email/unsubscribe/{category}?token={Uri.EscapeDataString(token)}";
                        message.Headers.Add("List-Unsubscribe", $"<{unsubUrl}>");
                        message.Headers.Add("List-Unsubscribe-Post", "List-Unsubscribe=One-Click");
                    }

                    await smtpClient.SendAsync(message);
                }
#endif

                await smtpClient.DisconnectAsync(true);
            }
            catch (Exception ex)
            {
                SendErrorReport(protocolLog, ex);
                throw;
            }
        }

        /// <summary>
        /// Sends a single email to an explicit To list (no unsubscribe headers).
        /// </summary>
        private async Task SendDirectEmail(string fromEmail, string fromDisplay,
            EmailInfo emailInfo, List<string> toList, ClaimsPrincipal user)
        {
            var subject = emailInfo.Subject;

            if (emailInfo.IsTestEmail)
            {
                var apiUser = await _userRepository.GetByUniqueIdAsync(
                    Models.User.GetUniqueIdFromClaims(user.Claims));
                toList = new List<string> { apiUser.Emails };
                subject = $"Test: {subject}";
            }

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(fromDisplay, fromEmail));
            message.Subject = subject;
            message.Body = ConvertImageFormat(emailInfo.HtmlBody);
            message.ReplyTo.Add(new MailboxAddress(fromDisplay, fromEmail));

#if DEBUG
            message.Bcc.Add(new MailboxAddress(null, "bill@cohad.org"));
            message.Bcc.Add(new MailboxAddress(null, "bilongtest@gmail.com"));
#else
            if (toList != null && toList.Count > 0)
            {
                foreach (var item in toList)
                    message.To.Add(new MailboxAddress("", item));
            }
#endif
            if (message.To.Count < 1)
                message.To.Add(new GroupAddress("Private Recipients"));

            var memoryStream = new MemoryStream();
            var logger = new ProtocolLogger(memoryStream);
            try
            {
                DoSmtpSend(message, logger);
            }
            catch (Exception ex)
            {
                SendErrorReport(memoryStream, ex);
                throw;
            }
        }

        private void DoSmtpSend(MimeMessage message, ProtocolLogger logger = null)
        {
            using var smtpClient = logger != null ? new SmtpClient(logger) : new SmtpClient();
            smtpClient.Connect(_options.SmtpHost, 587, MailKit.Security.SecureSocketOptions.StartTls);
            smtpClient.Authenticate(_options.SmtpUser, _options.SmtpPassword);
            smtpClient.Send(message);
        }

        private void SendErrorReport(MemoryStream protocolLog, Exception ex)
        {
            var errorMessage = new MimeMessage();
            errorMessage.From.Add(new MailboxAddress("", "bill@cohad.org"));
            errorMessage.To.Add(new MailboxAddress("", "bill@cohad.org"));
            errorMessage.Subject = "SendEmail failed";
            var bodyBuilder = new BodyBuilder();
            bodyBuilder.Attachments.Add("MailKitProtocolLog.txt", protocolLog.ToArray());
            bodyBuilder.TextBody = $"SendEmail failed. Protocol log attached. Exception:\n{ex}";
            errorMessage.Body = bodyBuilder.ToMessageBody();

            try
            {
                DoSmtpSend(errorMessage);
            }
            catch
            {
                // Suppress exceptions sending the error report
            }
        }

        private async Task<List<EmailRecipient>> GetAllEmailsMatchingFilter(Func<EmailAddress, bool> filter)
        {
            var seen = new Dictionary<string, EmailRecipient>(StringComparer.OrdinalIgnoreCase);
            var homes = await _homeRepository.GetAllAsync();

            foreach (var home in homes)
            {
                if (home.Residents != null)
                {
                    foreach (var resident in home.Residents)
                    {
                        if (resident.EmailAddresses == null) continue;
                        foreach (var addr in resident.EmailAddresses.Where(filter))
                        {
                            if (!string.IsNullOrWhiteSpace(addr.Address) && !seen.ContainsKey(addr.Address))
                                seen[addr.Address] = new EmailRecipient { Email = addr.Address, HomeId = home.Id };
                        }
                    }
                }

                if (filter(home.EmailAddress) && !string.IsNullOrWhiteSpace(home.EmailAddress?.Address))
                {
                    if (!seen.ContainsKey(home.EmailAddress.Address))
                        seen[home.EmailAddress.Address] = new EmailRecipient { Email = home.EmailAddress.Address, HomeId = home.Id };
                }
            }

            return seen.Values.ToList();
        }

        private string BuildUnsubscribeFooter(string category, string categoryDisplayName, string token)
        {
            if (string.IsNullOrEmpty(_appBaseUrl) || string.IsNullOrEmpty(token))
                return "";

            var prefsUrl = $"{_appBaseUrl}/email-preferences?token={Uri.EscapeDataString(token)}";

            return "\n<hr style=\"margin-top:32px;border:none;border-top:1px solid #ddd\">" +
                   "<p style=\"font-size:12px;color:#888;font-family:sans-serif;\">" +
                   $"You received this email because your address is subscribed to COHAD {categoryDisplayName} updates. " +
                   $"<a href=\"{prefsUrl}\" style=\"color:#1a73e8;\">Manage your email preferences</a>" +
                   "</p>";
        }

        // --- Image handling ---

        private static ExtractedImages ExtractInlineImages(string htmlBody)
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

        private static MimeEntity BuildBodyWithImages(string html, List<InlineImage> images)
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

        /// <summary>
        /// Legacy single-pass image conversion (used by direct sends without per-recipient tokens).
        /// </summary>
        private MimeEntity ConvertImageFormat(string htmlBody)
        {
            var extracted = ExtractInlineImages(htmlBody);
            return BuildBodyWithImages(extracted.ProcessedHtml, extracted.Images);
        }

        internal class EmailRecipient
        {
            public string Email { get; set; }
            public Guid HomeId { get; set; }
        }

        private class ExtractedImages
        {
            public string ProcessedHtml { get; set; }
            public List<InlineImage> Images { get; set; }
        }

        private class InlineImage
        {
            public string FileName { get; set; }
            public string ContentId { get; set; }
            public byte[] Data { get; set; }
        }
    }
}
