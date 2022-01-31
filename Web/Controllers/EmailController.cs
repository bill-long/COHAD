using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MailKit.Net.Smtp;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using MimeKit;
using MimeKit.Utils;
using Web.Configuration;
using Web.Models;
using Web.Repository;
using Web.UpdateModels;

namespace Web.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class EmailController : ControllerBase
    {
        private readonly CohadWebDbContext _dbContext;
        private readonly SmtpOptions _options;

        public EmailController(CohadWebDbContext dbContext, IConfiguration config)
        {
            _dbContext = dbContext;
            _options = new SmtpOptions
            {
                SmtpHost = config["SmtpHost"],
                SmtpUser = config["SmtpUser"],
                SmtpPassword = config["SmtpPassword"]
            };
        }

        [HttpPut("from-board")]
        [Authorize(Policy = "Board")]
        public async Task<IActionResult> SendEmailFromBoard([FromBody] EmailInfo emailInfo)
        {
            await AuditEmail("Board", emailInfo);

            await SendEmail("board@cohad.org", "COHAD Board", emailInfo, e => e != null && e.BoardEmailOptedIn);

            return Ok();
        }

        [HttpPut("from-welcome")]
        [Authorize(Policy = "WelcomeCommittee")]
        public async Task<IActionResult> SendEmailFromWelcomeCommittee([FromBody] EmailInfo emailInfo)
        {
            await AuditEmail("Welcome Committee", emailInfo);

            await SendEmail("welcome@cohad.org", "COHAD Welcome Committee", emailInfo, e => e != null && e.WelcomeEmailOptedIn);

            return Ok();
        }

        [HttpPut("from-garden")]
        [Authorize(Policy = "GardenClub")]
        public async Task<IActionResult> SendEmailFromSocialCommittee([FromBody] EmailInfo emailInfo)
        {
            await AuditEmail("Garden Club", emailInfo);

            await SendEmail("gardenclub@cohad.org", "COHAD Garden Club", emailInfo, e => e != null && e.GardenClubEmailOptedIn);

            return Ok();
        }

        [HttpPut("from-social")]
        [Authorize(Policy = "SocialCommittee")]
        public async Task<IActionResult> SendEmailFromGardenClub([FromBody] EmailInfo emailInfo)
        {
            await AuditEmail("Social Committee", emailInfo);

            await SendEmail("social@cohad.org", "COHAD Social Committee", emailInfo, e => e != null && e.SocialCommitteeEmailOptedIn);

            return Ok();
        }

        private async Task SendEmail(string fromEmail, string fromDisplay, EmailInfo emailInfo, Func<EmailAddress, bool> recipientFilter)
        {
            List<string> bccList = null;

            var subject = emailInfo.Subject;

            if (emailInfo.IsTestEmail)
            {
                var apiUser =
                    await _dbContext.Users.FindAsync(Models.User.GetUniqueIdFromClaims(User.Claims));
                bccList = new List<string> { apiUser.Emails };
                subject = $"Test: {subject}";
            }
            else
            {
                bccList = await GetAllEmailsMatchingFilter(recipientFilter);
            }

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(fromDisplay, fromEmail));
            message.Subject = subject;
            message.Body = ConvertImageFormat(emailInfo.HtmlBody);
            message.ReplyTo.Add(new MailboxAddress(fromDisplay, fromEmail));

            var homes = await _dbContext.Homes.ToListAsync();
#if DEBUG
            message.Bcc.Add(new MailboxAddress(null, "bill@cohad.org"));
#else
            foreach (var email in bccList)
            {
                message.Bcc.Add(new MailboxAddress(null, email));
            }
#endif
            using var smtpClient = new SmtpClient();
            smtpClient.Connect(_options.SmtpHost, 587, MailKit.Security.SecureSocketOptions.StartTls);
            smtpClient.Authenticate(_options.SmtpUser, _options.SmtpPassword);
            smtpClient.Send(message);
        }

        private async Task AuditEmail(string from, EmailInfo emailInfo)
        {
            var apiUser =
                await _dbContext.Users.FindAsync(Models.User.GetUniqueIdFromClaims(User.Claims));

            await _dbContext.AuditLog.AddAsync(new NewAuditLogEntry
            {
                Id = Guid.NewGuid(),
                SubjectId = "",
                SubjectName = $"Email recipient: {(emailInfo.IsTestEmail ? apiUser.Emails : "Neighborhood")}",
                Action = $"Sent email from {from}",
                Time = DateTime.UtcNow,
                UserDisplayName = $"{apiUser.GivenName ?? ""} {apiUser.Surname ?? ""}",
                UserId = apiUser.UniqueId
            });

            await _dbContext.SaveChangesAsync();
        }

        private async Task<List<string>> GetAllEmailsMatchingFilter(Func<EmailAddress, bool> filter)
        {
            var bccAddresses = new List<string>();
            var homes = await _dbContext.Homes.ToListAsync();

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

            bccAddresses = bccAddresses.Distinct().ToList();

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
