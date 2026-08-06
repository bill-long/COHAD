using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
            ILogger<UnsubscribeController> logger
        )
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
            [FromForm(Name = "List-Unsubscribe")] string listUnsubscribe
        )
        {
            if (!string.Equals(listUnsubscribe, "One-Click", StringComparison.Ordinal))
            {
                // Form binding collapses an empty value to null exactly as query binding does, so
                // the bound string cannot tell "the provider omitted the field" from "the field was
                // emptied in transit" - and that distinction is the whole subject of this work. Ask
                // the form collection, which still knows. The value itself is attacker-controlled
                // and is never recorded.
                var present = Request.HasFormContentType && Request.Form.ContainsKey("List-Unsubscribe");

                RecordRejection(
                    new UnsubscribeRejection
                    {
                        // A request that carried a token belongs to the token stream even though the
                        // body check rejected it before the token was read - otherwise a provider
                        // changing its RFC 8058 body, which is exactly the regression worth
                        // catching, would be billed to the budget an empty POST can flood.
                        Kind = UnsubscribeDiagnostics.ClassifyByTokenPresence(HttpContext),
                        Reason = present ? "confirmation-present-but-not-one-click" : "confirmation-field-absent",
                    }
                );

                return BadRequest(new { error = "Invalid or missing List-Unsubscribe confirmation." });
            }

            var payload = ResolveCredential(token);
            if (payload == null)
                return BadRequest(new { error = "Invalid or missing token." });

            if (!EmailSubscriptionCategories.TryGetCategorySetter(category, out var setter))
            {
                // The category comes from the route, so it is attacker-controlled; only whether it
                // was recognised is recorded, never the value.
                RecordPostCredentialRejection("unknown-category");
                return BadRequest(new { error = $"Unknown category: {category}" });
            }

            return await WithOptimisticRetry(
                payload,
                (home, matchingAddresses) =>
                {
                    foreach (var addr in matchingAddresses)
                        setter(addr, false);

                    return Ok(
                        new
                        {
                            message = $"Successfully unsubscribed from {EmailSubscriptionCategories.DisplayNames.GetValueOrDefault(category, category)} emails.",
                        }
                    );
                }
            );
        }

        /// <summary>
        /// Returns current email preferences for the token's home + email.
        /// </summary>
        [HttpGet("preferences")]
        public async Task<IActionResult> GetPreferences([FromQuery] string token)
        {
            var payload = ResolveCredential(token);
            if (payload == null)
                return BadRequest(new { error = "Invalid or missing token." });

            var (home, matchingAddresses, _, failure) = await LoadHomeAndMatchesAsync(payload);
            if (failure != null)
                return failure;

            // Aggregate preferences: show true if any matching address has it enabled
            var dto = new EmailPreferencesDto
            {
                Email = payload.Email,
                HomeName = $"{home.StreetNumber} {home.StreetName}",
                BoardEmailOptedIn = matchingAddresses.Any(a => a.BoardEmailOptedIn),
                WelcomeEmailOptedIn = matchingAddresses.Any(a => a.WelcomeEmailOptedIn),
                GardenClubEmailOptedIn = matchingAddresses.Any(a => a.GardenClubEmailOptedIn),
                SocialCommitteeEmailOptedIn = matchingAddresses.Any(a => a.SocialCommitteeEmailOptedIn),
                SunshineCommitteeEmailOptedIn = matchingAddresses.Any(a => a.SunshineCommitteeEmailOptedIn),
            };

            return Ok(dto);
        }

        /// <summary>
        /// Saves updated email preferences for the token's home + email.
        /// Only fields present in the request body are updated; omitted fields are left unchanged.
        /// </summary>
        [HttpPut("preferences")]
        public async Task<IActionResult> UpdatePreferences(
            [FromQuery] string token,
            [FromBody] UpdateEmailPreferencesDto dto
        )
        {
            var payload = ResolveCredential(token);
            if (payload == null)
                return BadRequest(new { error = "Invalid or missing token." });

            // Belt and braces. [ApiController] rejects an absent or unparseable body before this
            // action runs, so over HTTP this is unreachable and the middleware logs that case from
            // the status code. Kept so a direct caller cannot NRE below.
            //
            // Do NOT add `#nullable enable` to this file to "fix" the annotation. On an MVC action,
            // reference-type nullability is not documentation - it is binding semantics. Annotating
            // it made `category` implicitly required (a whitespace-mangled segment stopped reaching
            // the action, losing its classification) and flipped this parameter's EmptyBodyBehavior
            // to Allow (a wrong Content-Type stopped surfacing as 415). The contract-bearing types
            // are annotated in their own files, where they carry no binding behaviour.
            if (dto == null)
            {
                RecordPostCredentialRejection("missing-request-body");
                return BadRequest(new { error = "Request body is required." });
            }

            return await WithOptimisticRetry(
                payload,
                (home, matchingAddresses) =>
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
                }
            );
        }

        /// <summary>
        /// Resolves a presented credential to its payload, recording the outcome, and returns null
        /// when it is rejected.
        /// <para>
        /// Acceptances log here, at Information, which <c>appsettings.json</c> raises for this
        /// category so legacy redemptions stay countable. Rejections are only <em>recorded</em>;
        /// <see cref="UnsubscribeDiagnosticsMiddleware"/> logs them, because an action cannot see the
        /// failures the MVC pipeline produces before it runs and must not be the only place that
        /// knows how a rejection is written down.
        /// </para>
        /// <para>
        /// Every rejection is treated the same, regardless of reason. An earlier revision demoted
        /// the "no token supplied" case to Debug to blunt anonymous flooding, but ASP.NET Core binds
        /// an empty <c>?token=</c> to null exactly like an absent one, so that carve-out silenced
        /// the stripped-link signal this work exists to capture. Flood protection belongs in
        /// <see cref="IUnsubscribeWarningBudget"/>, which bounds volume without discarding a class
        /// of evidence.
        /// </para>
        /// </summary>
        private UnsubscribeTokenPayload ResolveCredential(string token)
        {
            var result = _tokenService.ValidateToken(token);

            if (result.IsValid)
            {
                _logger.LogInformation(
                    "Unsubscribe credential accepted for {Operation} (type {CredentialType}).",
                    UnsubscribeDiagnostics.DescribeEndpoint(HttpContext),
                    UnsubscribeDiagnostics.LegacyTokenCredential
                );
                return result.Payload;
            }

            // The token itself is a bearer credential and is never recorded - only its length and,
            // for tokens long enough that it identifies nothing, its sanitised ends.
            RecordRejection(
                new UnsubscribeRejection
                {
                    // The shared rule, not a hard-coded TokenRejection: a bare request with no
                    // token still reaches here (ValidateToken(null) returns Missing), and billing
                    // that to the token stream would let tokenless crawler noise drain the budget
                    // protecting real mangled-link evidence.
                    Kind = UnsubscribeDiagnostics.ClassifyByTokenPresence(HttpContext),
                    // Reason is not set: the middleware logs Failure for token rejections, and a
                    // second copy of the same value would be dead state nothing could catch drifting.
                    Failure = result.Failure,
                    TokenLength = token?.Length ?? 0,
                    TokenEnds = DescribeTokenEnds(token),
                }
            );

            return null;
        }

        /// <summary>
        /// Records a rejection that happened <em>after</em> the credential was accepted - a missing
        /// home, an address no longer on the home, an unknown category, a lost write race.
        /// <para>
        /// These returned 4xx silently, which left the most confusing case of all invisible: the
        /// token is valid, the resident still lands on "the link may be invalid or expired" because
        /// the SPA renders every failure identically, and the log showed only an acceptance. An
        /// operator reading it would conclude the request succeeded.
        /// </para>
        /// </summary>
        private void RecordPostCredentialRejection(string reason)
        {
            RecordRejection(new UnsubscribeRejection { Kind = UnsubscribeWarningKind.TokenRejection, Reason = reason });
        }

        private void RecordRejection(UnsubscribeRejection rejection)
        {
            UnsubscribeDiagnostics.Record(HttpContext, rejection);
        }

        /// <summary>
        /// Loads the home and its residents and finds the addresses matching the credential, or
        /// returns the rejection response - recorded for the diagnostics middleware - when either
        /// lookup comes up empty. Defined once because both endpoints need the identical sequence
        /// and a drifted copy is where the next silent 404 would hide.
        /// <para>
        /// The two reads are deliberately <b>sequential</b>, against the repo checklist's
        /// parallelise-independent-IO rule. Starting them together saves one round-trip but makes
        /// the resident read happen even when the home is missing, and a transient fault on it then
        /// masks a diagnosable "home-not-found" with an unlogged 500 - or, if the fault is merely
        /// abandoned, disappears entirely. Two prior revisions of this method shipped exactly those
        /// bugs. One round-trip on a page a resident opens once is not worth re-introducing them.
        /// </para>
        /// </summary>
        private async Task<(
            Home Home,
            List<EmailAddress> Addresses,
            HashSet<Resident> AffectedResidents,
            IActionResult Failure
        )> LoadHomeAndMatchesAsync(UnsubscribeTokenPayload payload)
        {
            var home = await _homeRepository.GetByIdAsync(payload.HomeId);
            if (home == null)
            {
                RecordPostCredentialRejection("home-not-found");
                return (null, null, null, NotFound(new { error = "Home not found." }));
            }

            var residents = await _residentRepository.GetByHomeIdAsync(payload.HomeId);
            var (matchingAddresses, affectedResidents) = FindMatchingEmailAddresses(home, residents, payload.Email);
            if (matchingAddresses.Count == 0)
            {
                RecordPostCredentialRejection("email-not-on-home");
                return (null, null, null, NotFound(new { error = "Email address not found on this home." }));
            }

            return (home, matchingAddresses, affectedResidents, null);
        }

        /// <summary>
        /// The shortest token whose first and last four characters may be logged. A legacy token is
        /// ~135 characters, so eight of them identify nothing; the typed recovery code Part 2
        /// introduces is nine, and head+tail is itself eight, so disclosing the ends of a short
        /// credential would hand over nearly all of it. Below this length the ends are withheld and
        /// the logged length carries the diagnosis on its own.
        /// </summary>
        internal const int MinLengthForEndDisclosure = 32;

        /// <summary>
        /// Describes a token's ends for logging without disclosing the token. Returns a marker for
        /// the absent, blank, and too-short-to-disclose cases; the length is logged separately.
        /// <para>
        /// "blank" is unreachable for the query-bound <c>token</c> parameter, because ASP.NET Core
        /// converts an empty or whitespace query value to null before the action runs. It is kept
        /// because this is a general helper and Part 2's typed code arrives in a form body, where
        /// empty strings survive binding.
        /// </para>
        /// </summary>
        internal static string DescribeTokenEnds(string token)
        {
            if (token == null)
                return "absent";
            if (string.IsNullOrWhiteSpace(token))
                return "blank";
            if (token.Length < MinLengthForEndDisclosure)
                return "withheld";
            return $"{SanitizeForLog(token[..4])}...{SanitizeForLog(token[^4..])}";
        }

        /// <summary>
        /// Replaces anything outside the base64url alphabet with '.', so disclosed characters cannot
        /// forge a log line. The token arrives from an anonymous query string, and a percent-encoded
        /// newline in it would otherwise split the rendered message in any line-oriented sink and
        /// let the caller author what looks like a second, genuine entry. A real token is base64url,
        /// so it survives this unchanged.
        /// </summary>
        private static string SanitizeForLog(string value)
        {
            return string.Create(
                value.Length,
                value,
                (destination, source) =>
                {
                    for (var i = 0; i < source.Length; i++)
                    {
                        var c = source[i];
                        destination[i] = char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_' ? c : '.';
                    }
                }
            );
        }

        /// <summary>
        /// Executes a read-modify-write on the home with optimistic concurrency retry.
        /// The action receives the freshly-loaded home and matching email addresses,
        /// applies modifications in place, and returns the IActionResult. If a
        /// concurrency conflict occurs, the entire cycle is retried.
        /// </summary>
        private async Task<IActionResult> WithOptimisticRetry(
            UnsubscribeTokenPayload payload,
            Func<Home, List<EmailAddress>, IActionResult> modifyAndRespond
        )
        {
            for (int attempt = 0; attempt < MaxRetries; attempt++)
            {
                var (home, matchingAddresses, affectedResidents, failure) = await LoadHomeAndMatchesAsync(payload);
                if (failure != null)
                    return failure;

                var result = modifyAndRespond(home, matchingAddresses);

                try
                {
                    await _homeRepository.UpsertAsync(home);
                    // Only upsert residents that own a matched email address.
                    try
                    {
                        foreach (var resident in affectedResidents)
                            await _residentRepository.UpsertAsync(resident);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(
                            ex,
                            "Failed to persist resident preference updates for home {HomeId} after home save succeeded",
                            payload.HomeId
                        );
                    }

                    return result;
                }
                catch (ConcurrencyConflictException)
                {
                    // Fall through to the next attempt, or out of the loop on the last one. The
                    // conflict response lives in exactly one place below; an earlier revision also
                    // returned it from inside the catch, which made this one unreachable and left
                    // two copies of the same response to drift apart.
                }
            }

            RecordPostCredentialRejection("concurrency-retries-exhausted");
            return Conflict(new { error = "Unable to save preferences due to concurrent updates. Please try again." });
        }

        private static (List<EmailAddress> Addresses, HashSet<Resident> Residents) FindMatchingEmailAddresses(
            Home home,
            List<Resident> residents,
            string email
        )
        {
            var matches = new List<EmailAddress>();
            var affectedResidents = new HashSet<Resident>();
            var normalizedEmail = email.Trim().ToLowerInvariant();

            if (residents != null)
            {
                foreach (var resident in residents)
                {
                    if (resident.EmailAddresses == null)
                        continue;

                    foreach (var addr in resident.EmailAddresses)
                    {
                        if (addr?.Address != null && addr.Address.Trim().ToLowerInvariant() == normalizedEmail)
                        {
                            matches.Add(addr);
                            affectedResidents.Add(resident);
                        }
                    }
                }
            }

            if (
                home.EmailAddress?.Address != null
                && home.EmailAddress.Address.Trim().ToLowerInvariant() == normalizedEmail
            )
            {
                matches.Add(home.EmailAddress);
            }

            return (matches, affectedResidents);
        }
    }
}
