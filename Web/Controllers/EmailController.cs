using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Web.Configuration;
using Web.Models;
using Web.Repository;
using Web.UpdateModels;

namespace Web.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize(Policy = "Administrator")]
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
        public async Task<IActionResult> SendEmailFromGardenClub([FromBody] EmailInfo emailInfo)
        {
            await AuditEmail("Garden Club", emailInfo);

            await SendEmail("gardenclub@cohad.org", "COHAD Garden Club", emailInfo, e => e != null && e.GardenClubEmailOptedIn);

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

            var message = new MailMessage
            {
                From = new MailAddress(fromEmail, fromDisplay),
                Subject = subject,
                IsBodyHtml = true,
                Body = emailInfo.HtmlBody
            };

            message.ReplyToList.Add(new MailAddress(fromEmail, fromDisplay));

            var homes = await _dbContext.Homes.ToListAsync();
#if DEBUG
            message.Bcc.Add("bill@selfish.net");
#else
            foreach (var email in bccList)
            {
                message.Bcc.Add(email);
            }
#endif

            var smtpClient = new SmtpClient(_options.SmtpHost)
            {
                Port = 587,
                EnableSsl = true,
                Credentials = new NetworkCredential(_options.SmtpUser, _options.SmtpPassword)
            };

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
    }
}
