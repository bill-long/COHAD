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
        Task SendEmail(string fromEmail, string fromDisplay, EmailInfo emailInfo, Func<EmailAddress, bool> recipientFilter, string bccDisplayName, ClaimsPrincipal user);

        Task SendEmail(string fromEmail, string fromDisplay, EmailInfo emailInfo, List<string> toList, ClaimsPrincipal user);
    }

    public class EmailService : IEmailService
    {
        private readonly IUserRepository _userRepository;
        private readonly IHomeRepository _homeRepository;
        private readonly SmtpOptions _options;

        public EmailService(IUserRepository userRepository, IHomeRepository homeRepository, IConfiguration config)
        {
            _userRepository = userRepository;
            _homeRepository = homeRepository;
            _options = new SmtpOptions
            {
                SmtpHost = config["SmtpHost"],
                SmtpUser = config["SmtpUser"],
                SmtpPassword = config["SmtpPassword"]
            };
        }

        public async Task SendEmail(string fromEmail, string fromDisplay, EmailInfo emailInfo, Func<EmailAddress, bool> bccRecipientFilter, string bccDisplayName, ClaimsPrincipal user)
        {
            List<string> bccList = await GetAllEmailsMatchingFilter(bccRecipientFilter);
            await SendEmail(fromEmail, fromDisplay, emailInfo, null, bccList, bccDisplayName, user);
        }

        public async Task SendEmail(string fromEmail, string fromDisplay, EmailInfo emailInfo, List<string> toList, ClaimsPrincipal user)
        {
            await SendEmail(fromEmail, fromDisplay, emailInfo, toList, null, null, user);
        }

        private async Task SendEmail(string fromEmail, string fromDisplay, EmailInfo emailInfo, List<string> toList, List<string> bccList, string bccDisplayName, ClaimsPrincipal user)
        {
            var subject = emailInfo.Subject;

            if (emailInfo.IsTestEmail)
            {
                var apiUser =
                    await _userRepository.GetByUniqueIdAsync(Models.User.GetUniqueIdFromClaims(user.Claims));
                bccList = new List<string> { apiUser.Emails };
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
                {
                    message.To.Add(new MailboxAddress("", item));
                }
            }

            if (bccList != null && bccList.Count > 0)
            {
                foreach (var email in bccList)
                {
                    message.Bcc.Add(new MailboxAddress(null, email));
                }
            }
#endif
            if (message.To.Count < 1)
            {
                if (!string.IsNullOrEmpty(bccDisplayName))
                {
                    message.To.Add(new GroupAddress(bccDisplayName));
                }
                else
                {
                    // This should only happen during debug/testing
                    message.To.Add(new GroupAddress("Private Recipients"));
                }
            }

            var memoryStream = new MemoryStream();
            var logger = new ProtocolLogger(memoryStream);
            try
            {
                DoSmtpSend(message, logger);
            }
            catch (Exception ex)
            {
                var errorMessage = new MimeMessage();
                errorMessage.From.Add(new MailboxAddress("", "bill@cohad.org"));
                errorMessage.To.Add(new MailboxAddress("", "bill@cohad.org"));
                errorMessage.Subject = "SendEmail failed";
                var bodyBuilder = new BodyBuilder();
                bodyBuilder.Attachments.Add("MailKitProtocolLog.txt", memoryStream.ToArray());
                bodyBuilder.TextBody = $"SendEmail failed. Protocol log attached. Exception:\n{ex}";
                errorMessage.Body = bodyBuilder.ToMessageBody();

                try
                {
                    DoSmtpSend(errorMessage);
                }
                catch
                {
                    // Supress exceptions sending the error
                }

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

        private async Task<List<string>> GetAllEmailsMatchingFilter(Func<EmailAddress, bool> filter)
        {
            var bccAddresses = new List<string>();
            var homes = await _homeRepository.GetAllAsync();

            bccAddresses.AddRange(
                homes.SelectMany(
                    h => h.Residents.SelectMany(
                        r => r.EmailAddresses
                            .Where(filter)
                        )
                    ).Select(e => e.Address)
                );

            bccAddresses.AddRange(
                homes.Select(
                    h => h.EmailAddress)
                    .Where(filter)
                    .Select(e => e.Address)
                );

            bccAddresses = bccAddresses.Distinct().Where(e => !string.IsNullOrWhiteSpace(e)).ToList();

            return bccAddresses;
        }

        private MimeEntity ConvertImageFormat(string htmlBody)
        {
            var imageStart = "<img src=\"data:";
            var imageEnd = "\">";

            var bodyBuilder = new BodyBuilder();
            var sb = new StringBuilder();

            var imageCount = 0;
            int position = 0;
            while (position < htmlBody.Length)
            {
                var nextImageStart = htmlBody.IndexOf(imageStart, position);
                if (nextImageStart < 0)
                {
                    sb.Append(htmlBody.AsSpan(position));
                    break;
                }
                else
                {
                    sb.Append(htmlBody.AsSpan(position, nextImageStart - position));

                    var imageTypeStartPos = nextImageStart + imageStart.Length;
                    var imageTypeEndPos = htmlBody.IndexOf(';', imageTypeStartPos);
                    var imageType = htmlBody[imageTypeStartPos..imageTypeEndPos];
                    var imageExtension = imageType.AsSpan(imageType.IndexOf('/') + 1);

                    var encodingStartPos = imageTypeEndPos + 1;
                    var encodingEndPos = htmlBody.IndexOf(',', encodingStartPos);
                    var encoding = htmlBody[encodingStartPos..encodingEndPos];
                    if (encoding != "base64")
                    {
                        throw new InvalidOperationException($"Unsupported image encoding: {encoding}");
                    }

                    var base64Start = encodingEndPos + 1;
                    var base64End = htmlBody.IndexOf(imageEnd, base64Start);
                    var base64 = htmlBody[base64Start..base64End];
                    var imageBytes = Convert.FromBase64String(base64);

                    var imageResource = bodyBuilder.LinkedResources.Add($"image{imageCount++}.{imageExtension}", imageBytes);
                    imageResource.ContentId = MimeUtils.GenerateMessageId();
                    sb.Append($"<img src=\"cid:{imageResource.ContentId}\">");

                    position = base64End + imageEnd.Length;
                }
            }

            bodyBuilder.HtmlBody = sb.ToString();
            return bodyBuilder.ToMessageBody();
        }
    }
}
