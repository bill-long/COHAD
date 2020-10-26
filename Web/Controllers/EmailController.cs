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
    [Authorize(Policy = "Committee")]
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

        [HttpPut]
        public async Task<IActionResult> SendEmail([FromBody] EmailInfo emailInfo)
        {
            var message = new MailMessage
            {
                From = new MailAddress("board@cohad.org", "Canyon Oaks HOA"),
                Subject = emailInfo.Subject,
                IsBodyHtml = true,
                Body = emailInfo.HtmlBody
            };

            message.ReplyToList.Add(new MailAddress("board@cohad.org", "COHAD Board"));
            message.ReplyToList.Add(new MailAddress("redacted@example.com", "Judy Johannesen"));

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

            return Ok();
        }
    }
}
