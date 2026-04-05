using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Web.Models;
using Web.PresentationModels;
using Web.Services;
using Web.Services.Repositories;

namespace Web.Controllers
{
    [Route("api/email")]
    [ApiController]
    [AllowAnonymous]
    public class UnsubscribeController : ControllerBase
    {
        private const int MaxRetries = 3;

        private readonly IUnsubscribeTokenService _tokenService;
        private readonly IHomeRepository _homeRepository;
        private readonly IResidentRepository _residentRepository;
        private readonly ILogger<UnsubscribeController> _logger;

        public UnsubscribeController(
            IUnsubscribeTokenService tokenService,
            IHomeRepository homeRepository,
            IResidentRepository residentRepository,
            ILogger<UnsubscribeController> logger)
        {
            _tokenService = tokenService;
            _homeRepository = homeRepository;
            _residentRepository = residentRepository;
            _logger = logger;
        }

        /// <summary>
        /// One-click unsubscribe (RFC 8058). Gmail/Yahoo send POST with body
        /// "List-Unsubscribe=One-Click" to the URL from the List-Unsubscribe header.
        /// </summary>
        [HttpPost("unsubscribe/{category}")]
        [Consumes("application/x-www-form-urlencoded")]
        public async Task<IActionResult> OneClickUnsubscribe(
            string category,
            [FromQuery] string token,
            [FromForm(Name = "List-Unsubscribe")] string listUnsubscribe)
        {
            if (!string.Equals(listUnsubscribe, "One-Click", StringComparison.Ordinal))
                return BadRequest(new { error = "Invalid or missing List-Unsubscribe confirmation." });

            var payload = _tokenService.ValidateToken(token);
            if (payload == null)
                return BadRequest(new { error = "Invalid or missing token." });

            if (!EmailSubscriptionCategories.TryGetCategorySetter(category, out var setter))
                return BadRequest(new { error = $"Unknown category: {category}" });

            return await WithOptimisticRetry(payload, (home, matchingAddresses) =>
            {
                foreach (var addr in matchingAddresses)
                    setter(addr, false);

                return Ok(new { message = $"Successfully unsubscribed from {EmailSubscriptionCategories.DisplayNames.GetValueOrDefault(category, category)} emails." });
            });
        }

        /// <summary>
        /// Returns current email preferences for the token's home + email.
        /// </summary>
        [HttpGet("preferences")]
        public async Task<IActionResult> GetPreferences([FromQuery] string token)
        {
            var payload = _tokenService.ValidateToken(token);
            if (payload == null)
                return BadRequest(new { error = "Invalid or missing token." });

            var home = await _homeRepository.GetByIdAsync(payload.HomeId);
            if (home == null)
                return NotFound(new { error = "Home not found." });

            var residents = await _residentRepository.GetByHomeIdAsync(payload.HomeId);
            var matchingAddresses = FindMatchingEmailAddresses(home, residents, payload.Email);
            if (matchingAddresses.Count == 0)
                return NotFound(new { error = "Email address not found on this home." });

            // Aggregate preferences: show true if any matching address has it enabled
            var dto = new EmailPreferencesDto
            {
                Email = payload.Email,
                HomeName = $"{home.StreetNumber} {home.StreetName}",
                BoardEmailOptedIn = matchingAddresses.Any(a => a.BoardEmailOptedIn),
                WelcomeEmailOptedIn = matchingAddresses.Any(a => a.WelcomeEmailOptedIn),
                GardenClubEmailOptedIn = matchingAddresses.Any(a => a.GardenClubEmailOptedIn),
                SocialCommitteeEmailOptedIn = matchingAddresses.Any(a => a.SocialCommitteeEmailOptedIn),
                SunshineCommitteeEmailOptedIn = matchingAddresses.Any(a => a.SunshineCommitteeEmailOptedIn)
            };

            return Ok(dto);
        }

        /// <summary>
        /// Saves updated email preferences for the token's home + email.
        /// Only fields present in the request body are updated; omitted fields are left unchanged.
        /// </summary>
        [HttpPut("preferences")]
        public async Task<IActionResult> UpdatePreferences([FromQuery] string token, [FromBody] UpdateEmailPreferencesDto dto)
        {
            var payload = _tokenService.ValidateToken(token);
            if (payload == null)
                return BadRequest(new { error = "Invalid or missing token." });

            if (dto == null)
                return BadRequest(new { error = "Request body is required." });

            return await WithOptimisticRetry(payload, (home, matchingAddresses) =>
            {
                foreach (var addr in matchingAddresses)
                {
                    if (dto.BoardEmailOptedIn.HasValue)
                        addr.BoardEmailOptedIn = dto.BoardEmailOptedIn.Value;
                    if (dto.WelcomeEmailOptedIn.HasValue)
                        addr.WelcomeEmailOptedIn = dto.WelcomeEmailOptedIn.Value;
                    if (dto.GardenClubEmailOptedIn.HasValue)
                        addr.GardenClubEmailOptedIn = dto.GardenClubEmailOptedIn.Value;
                    if (dto.SocialCommitteeEmailOptedIn.HasValue)
                        addr.SocialCommitteeEmailOptedIn = dto.SocialCommitteeEmailOptedIn.Value;
                    if (dto.SunshineCommitteeEmailOptedIn.HasValue)
                        addr.SunshineCommitteeEmailOptedIn = dto.SunshineCommitteeEmailOptedIn.Value;
                }

                return Ok(new { message = "Preferences updated." });
            });
        }

        /// <summary>
        /// Executes a read-modify-write on the home with optimistic concurrency retry.
        /// The action receives the freshly-loaded home and matching email addresses,
        /// applies modifications in place, and returns the IActionResult. If a
        /// concurrency conflict occurs, the entire cycle is retried.
        /// </summary>
        private async Task<IActionResult> WithOptimisticRetry(
            UnsubscribeTokenPayload payload,
            Func<Home, List<EmailAddress>, IActionResult> modifyAndRespond)
        {
            for (int attempt = 0; attempt < MaxRetries; attempt++)
            {
                var home = await _homeRepository.GetByIdAsync(payload.HomeId);
                if (home == null)
                    return NotFound(new { error = "Home not found." });

                var residents = await _residentRepository.GetByHomeIdAsync(payload.HomeId);
                var matchingAddresses = FindMatchingEmailAddresses(home, residents, payload.Email);
                if (matchingAddresses.Count == 0)
                    return NotFound(new { error = "Email address not found on this home." });

                var result = modifyAndRespond(home, matchingAddresses);

                try
                {
                    await _homeRepository.UpsertAsync(home);
                    // Save all residents that might have had email preferences modified.
                    // If a resident upsert fails after the home save, log the error but
                    // still return the result — the home update was already committed.
                    try
                    {
                        foreach (var resident in residents)
                            await _residentRepository.UpsertAsync(resident);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to persist resident preference updates for home {HomeId} after home save succeeded", payload.HomeId);
                    }

                    return result;
                }
                catch (ConcurrencyConflictException)
                {
                    if (attempt >= MaxRetries - 1)
                        return Conflict(new { error = "Unable to save preferences due to concurrent updates. Please try again." });
                    // Otherwise retry with fresh data
                }
            }

            return Conflict(new { error = "Unable to save preferences due to concurrent updates. Please try again." });
        }

        private static List<EmailAddress> FindMatchingEmailAddresses(Home home, List<Resident> residents, string email)
        {
            var matches = new List<EmailAddress>();
            var normalizedEmail = email.Trim().ToLowerInvariant();

            if (residents != null)
            {
                foreach (var resident in residents)
                {
                    if (resident.EmailAddresses == null)
                        continue;

                    foreach (var addr in resident.EmailAddresses)
                    {
                        if (addr?.Address != null &&
                            addr.Address.Trim().ToLowerInvariant() == normalizedEmail)
                        {
                            matches.Add(addr);
                        }
                    }
                }
            }

            if (home.EmailAddress?.Address != null &&
                home.EmailAddress.Address.Trim().ToLowerInvariant() == normalizedEmail)
            {
                matches.Add(home.EmailAddress);
            }

            return matches;
        }
    }
}
