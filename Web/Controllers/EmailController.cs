using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Web.Configuration;
using Web.Models;
using Web.Services;
using Web.Services.Repositories;
using Web.UpdateModels;

namespace Web.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class EmailController : ControllerBase
    {
        private readonly IUserRepository _userRepository;
        private readonly IAuditLogRepository _auditLogRepository;
        private readonly IEmailService _emailService;

        public EmailController(IUserRepository userRepository, IAuditLogRepository auditLogRepository, IEmailService emailService)
        {
            _userRepository = userRepository;
            _auditLogRepository = auditLogRepository;
            _emailService = emailService;
        }

        [HttpPut("from-board")]
        [Authorize(Policy = "Board")]
        public async Task<IActionResult> SendEmailFromBoard([FromBody] EmailInfo emailInfo)
        {
            await AuditEmail("Board", emailInfo);

            await _emailService.SendEmail("board@cohad.org", "COHAD Board", emailInfo, e => e != null && e.BoardEmailOptedIn, "board", User);

            return Ok();
        }

        [HttpPut("from-welcome")]
        [Authorize(Policy = "WelcomeCommittee")]
        public async Task<IActionResult> SendEmailFromWelcomeCommittee([FromBody] EmailInfo emailInfo)
        {
            await AuditEmail("Welcome Committee", emailInfo);

            await _emailService.SendEmail("welcome@cohad.org", "COHAD Welcome Committee", emailInfo, e => e != null && e.WelcomeEmailOptedIn, "welcome", User);

            return Ok();
        }

        [HttpPut("from-garden")]
        [Authorize(Policy = "GardenClub")]
        public async Task<IActionResult> SendEmailFromGardenClub([FromBody] EmailInfo emailInfo)
        {
            await AuditEmail("Garden Club", emailInfo);

            await _emailService.SendEmail("gardenclub@cohad.org", "COHAD Garden Club", emailInfo, e => e != null && e.GardenClubEmailOptedIn, "garden", User);

            return Ok();
        }

        [HttpPut("from-social")]
        [Authorize(Policy = "SocialCommittee")]
        public async Task<IActionResult> SendEmailFromSocialCommittee([FromBody] EmailInfo emailInfo)
        {
            await AuditEmail("Social Committee", emailInfo);

            await _emailService.SendEmail("social@cohad.org", "COHAD Social Committee", emailInfo, e => e != null && e.SocialCommitteeEmailOptedIn, "social", User);

            return Ok();
        }

        [HttpPut("from-sunshine")]
        [Authorize(Policy = "SunshineCommittee")]
        public async Task<IActionResult> SendEmailFromSunshineCommittee([FromBody] EmailInfo emailInfo)
        {
            await AuditEmail("Sunshine Committee", emailInfo);

            await _emailService.SendEmail("sunshine@cohad.org", "COHAD Sunshine Committee", emailInfo, e => e != null && e.SunshineCommitteeEmailOptedIn, "sunshine", User);

            return Ok();
        }

        private async Task AuditEmail(string from, EmailInfo emailInfo)
        {
            var apiUser =
                await _userRepository.GetByUniqueIdAsync(Models.User.GetUniqueIdFromClaims(User.Claims));

            await _auditLogRepository.AddAsync(new NewAuditLogEntry
            {
                Id = Guid.NewGuid(),
                SubjectId = "",
                SubjectName = $"Email recipient: {(emailInfo.IsTestEmail ? apiUser.Emails : "Neighborhood")}",
                Action = $"Sent email from {from}",
                Time = DateTime.UtcNow,
                UserDisplayName = $"{apiUser.GivenName ?? ""} {apiUser.Surname ?? ""}",
                UserId = apiUser.UniqueId
            });
        }        
    }
}
