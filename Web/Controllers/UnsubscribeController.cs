using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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

        public UnsubscribeController(IUnsubscribeTokenService tokenService, IHomeRepository homeRepository)
        {
            _tokenService = tokenService;
            _homeRepository = homeRepository;
        }

        /// <summary>
        /// One-click unsubscribe (RFC 8058). Gmail/Yahoo send POST with body
        /// "List-Unsubscribe=One-Click" to the URL from the List-Unsubscribe header.
        /// </summary>
        [HttpPost("unsubscribe/{category}")]
        public async Task<IActionResult> OneClickUnsubscribe(string category, [FromQuery] string token)
        {
            var payload = _tokenService.ValidateToken(token);
            if (payload == null)
                return BadRequest(new { error = "Invalid or missing token." });

            if (!TryGetCategorySetter(category, out var setter))
                return BadRequest(new { error = $"Unknown category: {category}" });

            return await WithOptimisticRetry(payload, (home, matchingAddresses) =>
            {
                foreach (var addr in matchingAddresses)
                    setter(addr, false);

                return Ok(new { message = $"Successfully unsubscribed from {FormatCategoryName(category)} emails." });
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

            var matchingAddresses = FindMatchingEmailAddresses(home, payload.Email);
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
        /// </summary>
        [HttpPut("preferences")]
        public async Task<IActionResult> UpdatePreferences([FromQuery] string token, [FromBody] EmailPreferencesDto dto)
        {
            var payload = _tokenService.ValidateToken(token);
            if (payload == null)
                return BadRequest(new { error = "Invalid or missing token." });

            return await WithOptimisticRetry(payload, (home, matchingAddresses) =>
            {
                foreach (var addr in matchingAddresses)
                {
                    addr.BoardEmailOptedIn = dto.BoardEmailOptedIn;
                    addr.WelcomeEmailOptedIn = dto.WelcomeEmailOptedIn;
                    addr.GardenClubEmailOptedIn = dto.GardenClubEmailOptedIn;
                    addr.SocialCommitteeEmailOptedIn = dto.SocialCommitteeEmailOptedIn;
                    addr.SunshineCommitteeEmailOptedIn = dto.SunshineCommitteeEmailOptedIn;
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

                var matchingAddresses = FindMatchingEmailAddresses(home, payload.Email);
                if (matchingAddresses.Count == 0)
                    return NotFound(new { error = "Email address not found on this home." });

                var result = modifyAndRespond(home, matchingAddresses);

                try
                {
                    await _homeRepository.UpsertAsync(home);
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

        private static List<EmailAddress> FindMatchingEmailAddresses(Home home, string email)
        {
            var matches = new List<EmailAddress>();
            var normalizedEmail = email.Trim().ToLowerInvariant();

            if (home.Residents != null)
            {
                foreach (var resident in home.Residents)
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

        internal static readonly Dictionary<string, string> CategoryDisplayNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ["board"] = "Board",
            ["welcome"] = "Welcome Committee",
            ["garden"] = "Garden Club",
            ["social"] = "Social Committee",
            ["sunshine"] = "Sunshine Committee"
        };

        private static string FormatCategoryName(string category)
        {
            return CategoryDisplayNames.TryGetValue(category, out var name) ? name : category;
        }

        private static bool TryGetCategorySetter(string category, out Action<EmailAddress, bool> setter)
        {
            switch (category?.ToLowerInvariant())
            {
                case "board":
                    setter = (a, v) => a.BoardEmailOptedIn = v;
                    return true;
                case "welcome":
                    setter = (a, v) => a.WelcomeEmailOptedIn = v;
                    return true;
                case "garden":
                    setter = (a, v) => a.GardenClubEmailOptedIn = v;
                    return true;
                case "social":
                    setter = (a, v) => a.SocialCommitteeEmailOptedIn = v;
                    return true;
                case "sunshine":
                    setter = (a, v) => a.SunshineCommitteeEmailOptedIn = v;
                    return true;
                default:
                    setter = null;
                    return false;
            }
        }
    }
}
