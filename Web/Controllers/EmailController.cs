using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Web.Configuration;
using Web.Models;
using Web.Repository;
using Web.Services;
using Web.UpdateModels;

namespace Web.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class EmailController : ControllerBase
    {
        private readonly CohadWebDbContext _dbContext;
        private readonly SmtpOptions _options;
        private readonly IEmailService _emailService;

        public EmailController(CohadWebDbContext dbContext, IEmailService emailService)
        {
            _dbContext = dbContext;
            _emailService = emailService;
        }

        [HttpPut("from-board")]
        [Authorize(Policy = "Board")]
        public async Task<IActionResult> SendEmailFromBoard([FromBody] EmailInfo emailInfo)
        {
            await AuditEmail("Board", emailInfo);

            await _emailService.SendEmail("board@cohad.org", "COHAD Board", emailInfo, e => e != null && e.BoardEmailOptedIn, "Canyon Oaks Residents", User);

            return Ok();
        }

        [HttpPut("from-welcome")]
        [Authorize(Policy = "WelcomeCommittee")]
        public async Task<IActionResult> SendEmailFromWelcomeCommittee([FromBody] EmailInfo emailInfo)
        {
            await AuditEmail("Welcome Committee", emailInfo);

            await _emailService.SendEmail("welcome@cohad.org", "COHAD Welcome Committee", emailInfo, e => e != null && e.WelcomeEmailOptedIn, "Canyon Oaks Residents", User);

            return Ok();
        }

        [HttpPut("from-garden")]
        [Authorize(Policy = "GardenClub")]
        public async Task<IActionResult> SendEmailFromSocialCommittee([FromBody] EmailInfo emailInfo)
        {
            await AuditEmail("Garden Club", emailInfo);

            await _emailService.SendEmail("gardenclub@cohad.org", "COHAD Garden Club", emailInfo, e => e != null && e.GardenClubEmailOptedIn, "Canyon Oaks Residents", User);

            return Ok();
        }

        [HttpPut("from-social")]
        [Authorize(Policy = "SocialCommittee")]
        public async Task<IActionResult> SendEmailFromGardenClub([FromBody] EmailInfo emailInfo)
        {
            await AuditEmail("Social Committee", emailInfo);

            await _emailService.SendEmail("social@cohad.org", "COHAD Social Committee", emailInfo, e => e != null && e.SocialCommitteeEmailOptedIn, "Canyon Oaks Residents", User);

            return Ok();
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
    }
}
