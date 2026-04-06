using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Configuration;
using MimeKit;
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
        Task SendEmail(
            string fromEmail,
            string fromDisplay,
            EmailInfo emailInfo,
            Func<EmailAddress, bool> recipientFilter,
            string category,
            ClaimsPrincipal user
        );

        /// <summary>
        /// Sends an email to an explicit list of addresses (no unsubscribe headers).
        /// Used for transactional/system emails.
        /// </summary>
        Task SendEmail(
            string fromEmail,
            string fromDisplay,
            EmailInfo emailInfo,
            List<string> toList,
            ClaimsPrincipal user
        );
    }

    public class EmailService : IEmailService
    {
        private readonly IUserRepository _userRepository;
        private readonly IHomeRepository _homeRepository;
        private readonly IResidentRepository _residentRepository;
        private readonly IUnsubscribeTokenService _tokenService;
        private readonly SmtpOptions _options;
        private readonly string _appBaseUrl;

        public EmailService(
            IUserRepository userRepository,
            IHomeRepository homeRepository,
            IResidentRepository residentRepository,
            IUnsubscribeTokenService tokenService,
            IConfiguration config
        )
        {
            _userRepository = userRepository;
            _homeRepository = homeRepository;
            _residentRepository = residentRepository;
            _tokenService = tokenService;
            _options = new SmtpOptions
            {
                SmtpHost = config["SmtpHost"],
                SmtpUser = config["SmtpUser"],
                SmtpPassword = config["SmtpPassword"],
            };
            _appBaseUrl = (config["AppBaseUrl"] ?? "").TrimEnd('/');
        }

        public async Task SendEmail(
            string fromEmail,
            string fromDisplay,
            EmailInfo emailInfo,
            Func<EmailAddress, bool> recipientFilter,
            string category,
            ClaimsPrincipal user
        )
        {
            var recipients = await GetAllEmailsMatchingFilter(recipientFilter);
            await SendPerRecipientEmails(fromEmail, fromDisplay, emailInfo, recipients, category, user);
        }

        public async Task SendEmail(
            string fromEmail,
            string fromDisplay,
            EmailInfo emailInfo,
            List<string> toList,
            ClaimsPrincipal user
        )
        {
            await SendDirectEmail(fromEmail, fromDisplay, emailInfo, toList, user);
        }

        /// <summary>
        /// Sends individual emails per recipient with unsubscribe headers and footer.
        /// </summary>
        private async Task SendPerRecipientEmails(
            string fromEmail,
            string fromDisplay,
            EmailInfo emailInfo,
            List<EmailRecipient> recipients,
            string category,
            ClaimsPrincipal user
        )
        {
            var subject = emailInfo.Subject;
            List<EmailRecipient> recipientList = recipients;

            if (emailInfo.IsTestEmail)
            {
                var apiUser = await _userRepository.GetByUniqueIdAsync(Models.User.GetUniqueIdFromClaims(user.Claims));
                var testHomeId = apiUser.OwnedHomeIds?.Count > 0 ? apiUser.OwnedHomeIds[0] : Guid.Empty;
                recipientList = new List<EmailRecipient>
                {
                    new EmailRecipient { Email = apiUser.Emails, HomeId = testHomeId },
                };
                subject = $"Test: {subject}";
            }

            if (recipientList.Count == 0)
                return;

            var htmlBody = EmailMessageBuilder.InlineCss(emailInfo.HtmlBody ?? string.Empty);
            var imageData = EmailMessageBuilder.ExtractInlineImages(htmlBody);
            var categoryDisplayName = EmailSubscriptionCategories.DisplayNames.TryGetValue(category ?? "", out var name)
                ? name
                : category;

            var protocolLog = new MemoryStream();
            var logger = new ProtocolLogger(protocolLog);
            try
            {
                using var smtpClient = new SmtpClient(logger);
                await smtpClient.ConnectAsync(_options.SmtpHost, 587, MailKit.Security.SecureSocketOptions.StartTls);
                await smtpClient.AuthenticateAsync(_options.SmtpUser, _options.SmtpPassword);

#if DEBUG
                // In DEBUG, send a single representative message to test addresses
                // instead of one per recipient, to avoid spamming debug inboxes.
                var debugRecipient = recipientList[0];
                var debugToken =
                    (debugRecipient.HomeId != Guid.Empty && !string.IsNullOrEmpty(_appBaseUrl))
                        ? _tokenService.GenerateToken(debugRecipient.HomeId, debugRecipient.Email)
                        : null;
                var debugFooter = BuildUnsubscribeFooter(categoryDisplayName, debugToken);
                var debugMessage = new MimeMessage();
                debugMessage.From.Add(new MailboxAddress(fromDisplay, fromEmail));
                debugMessage.Subject = $"[DEBUG {recipientList.Count} recipients] {subject}";
                debugMessage.ReplyTo.Add(new MailboxAddress(fromDisplay, fromEmail));
                debugMessage.Bcc.Add(new MailboxAddress(null, "bill@cohad.org"));
                debugMessage.Bcc.Add(new MailboxAddress(null, "bilongtest@gmail.com"));
                debugMessage.To.Add(new GroupAddress("Private Recipients"));
                debugMessage.Body = EmailMessageBuilder.BuildBodyWithImages(
                    imageData.ProcessedHtml + debugFooter,
                    imageData.Images
                );
                if (debugToken != null && !string.IsNullOrEmpty(_appBaseUrl))
                {
                    var unsubUrl =
                        $"{_appBaseUrl}/api/email/unsubscribe/{category}?token={Uri.EscapeDataString(debugToken)}";
                    debugMessage.Headers.Add("List-Unsubscribe", $"<{unsubUrl}>");
                    debugMessage.Headers.Add("List-Unsubscribe-Post", "List-Unsubscribe=One-Click");
                }
                await smtpClient.SendAsync(debugMessage);
#else
                foreach (var recipient in recipientList)
                {
                    // Reset protocol log between messages to bound memory usage
                    protocolLog.SetLength(0);

                    var token =
                        (recipient.HomeId != Guid.Empty && !string.IsNullOrEmpty(_appBaseUrl))
                            ? _tokenService.GenerateToken(recipient.HomeId, recipient.Email)
                            : null;

                    var footer = BuildUnsubscribeFooter(categoryDisplayName, token);
                    var htmlWithFooter = imageData.ProcessedHtml + footer;

                    var message = new MimeMessage();
                    message.From.Add(new MailboxAddress(fromDisplay, fromEmail));
                    message.Subject = subject;
                    message.ReplyTo.Add(new MailboxAddress(fromDisplay, fromEmail));
                    message.To.Add(new MailboxAddress("", recipient.Email));

                    message.Body = EmailMessageBuilder.BuildBodyWithImages(htmlWithFooter, imageData.Images);

                    if (token != null && !string.IsNullOrEmpty(_appBaseUrl))
                    {
                        var unsubUrl =
                            $"{_appBaseUrl}/api/email/unsubscribe/{category}?token={Uri.EscapeDataString(token)}";
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
        private async Task SendDirectEmail(
            string fromEmail,
            string fromDisplay,
            EmailInfo emailInfo,
            List<string> toList,
            ClaimsPrincipal user
        )
        {
            var subject = emailInfo.Subject;

            if (emailInfo.IsTestEmail)
            {
                var apiUser = await _userRepository.GetByUniqueIdAsync(Models.User.GetUniqueIdFromClaims(user.Claims));
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
            var allResidents = await _residentRepository.GetAllAsync();
            var residentsByHome = allResidents.GroupBy(r => r.HomeId).ToDictionary(g => g.Key, g => g.ToList());

            foreach (var home in homes)
            {
                if (residentsByHome.TryGetValue(home.Id, out var residents))
                {
                    foreach (var resident in residents)
                    {
                        if (resident.EmailAddresses == null)
                            continue;
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
                        seen[home.EmailAddress.Address] = new EmailRecipient
                        {
                            Email = home.EmailAddress.Address,
                            HomeId = home.Id,
                        };
                }
            }

            return seen.Values.ToList();
        }

        private string BuildUnsubscribeFooter(string categoryDisplayName, string token)
        {
            return EmailMessageBuilder.BuildUnsubscribeFooter(_appBaseUrl, categoryDisplayName, token);
        }

        /// <summary>
        /// Legacy single-pass image conversion (used by direct sends without per-recipient tokens).
        /// </summary>
        private static MimeEntity ConvertImageFormat(string htmlBody)
        {
            var inlinedHtml = EmailMessageBuilder.InlineCss(htmlBody ?? string.Empty);
            var extracted = EmailMessageBuilder.ExtractInlineImages(inlinedHtml);
            return EmailMessageBuilder.BuildBodyWithImages(extracted.ProcessedHtml, extracted.Images);
        }

        internal class EmailRecipient
        {
            public string Email { get; set; }
            public Guid HomeId { get; set; }
        }
    }
}
