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
            await SendEmail("board@cohad.org", "COHAD Board", emailInfo.Subject, emailInfo.HtmlBody);

            return Ok();
        }

        [HttpPut("from-social")]
        [Authorize(Policy = "SocialCommittee")]
        public async Task<IActionResult> SendEmailFromSocialCommittee([FromBody] EmailInfo emailInfo)
        {
            await SendEmail("social@cohad.org", "COHAD Social Committee", emailInfo.Subject, emailInfo.HtmlBody);

            return Ok();
        }

        [HttpPut("from-garden")]
        [Authorize(Policy = "GardenClub")]
        public async Task<IActionResult> SendEmailFromGardenClub([FromBody] EmailInfo emailInfo)
        {
            await SendEmail("gardenclub@cohad.org", "COHAD Garden Club", emailInfo.Subject, emailInfo.HtmlBody);

            return Ok();
        }

        private async Task SendEmail(string fromEmail, string fromDisplay, string subject, string htmlBody)
        {
            var message = new MailMessage
            {
                From = new MailAddress(fromEmail, fromDisplay),
                Subject = subject,
                IsBodyHtml = true,
                Body = htmlBody
            };

            message.ReplyToList.Add(new MailAddress(fromEmail, fromDisplay));

            var homes = await _dbContext.Homes.ToListAsync();
            var bccAddresses =
                homes.SelectMany(h => h.Residents.SelectMany(r => r.EmailAddresses.Where(e => e.GroupEmailOptedIn))).ToList();

#if DEBUG
            message.Bcc.Add("bill@selfish.net");
#else
            foreach (var email in bccAddresses)
            {
                message.Bcc.Add(email.Address);
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
    }
}
