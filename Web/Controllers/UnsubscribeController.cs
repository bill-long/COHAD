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

        private readonly IUnsubscribeCredentialResolver _credentialResolver;
        private readonly IHomeRepository _homeRepository;
        private readonly IResidentRepository _residentRepository;
        private readonly IEmailSuppressionService _suppressionService;
        private readonly IEmailSuppressionRepository _suppressionRepository;
        private readonly ILogger<UnsubscribeController> _logger;

        public UnsubscribeController(
            IUnsubscribeCredentialResolver credentialResolver,
            IHomeRepository homeRepository,
            IResidentRepository residentRepository,
            IEmailSuppressionService suppressionService,
            IEmailSuppressionRepository suppressionRepository,
            ILogger<UnsubscribeController> logger
        )
        {
            _credentialResolver = credentialResolver;
            _homeRepository = homeRepository;
            _residentRepository = residentRepository;
            _suppressionService = suppressionService;
            _suppressionRepository = suppressionRepository;
            _logger = logger;
        }

        /// <summary>
        /// One-click unsubscribe (RFC 8058). Gmail/Yahoo send POST with body
        /// "List-Unsubscribe=One-Click" to the URL from the List-Unsubscribe header.
        /// <para>
        /// Writes an all-mail suppression, not a category boolean. A recipient who clicks
        /// Unsubscribe in Gmail is asking for the mail to stop; clearing one of five booleans
        /// scattered the answer across every home carrying the address, destroyed stated
        /// preferences to record a fact that is not a preference, and left nothing to restore.
        /// The suppression is keyed on the address alone, so no home or resident is read or
        /// written here at all - the preferences survive underneath it intact, and the send
        /// path's single enforcement point is what makes the mail actually stop. The route keeps
        /// its <c>{category}</c> segment because it is baked into every List-Unsubscribe header
        /// already sitting in inboxes.
        /// </para>
        /// </summary>
        [HttpPost("unsubscribe/{category}")]
        [Consumes("application/x-www-form-urlencoded")]
        public async Task<IActionResult> OneClickUnsubscribe(
            string category,
            [FromQuery] string token,
            [FromQuery(Name = "u")] string linkId,
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

            var credential = await ResolveCredentialAsync(token, linkId);
            if (credential == null)
                return BadRequest(new { error = "Invalid or missing credential." });

            if (!EmailSubscriptionCategories.IsKnownCategory(category))
            {
                // Still rejected even though the suppression is category-free: the segment is part
                // of a URL we emitted, so an unrecognised value means the link was mangled in
                // transit - exactly the evidence Part 1 exists to classify - or forged. The
                // category comes from the route and is attacker-controlled; only whether it was
                // recognised is recorded, never the value.
                RecordPostCredentialRejection("unknown-category");
                return BadRequest(new { error = $"Unknown category: {category}" });
            }

            try
            {
                // SuppressedBy is the credential type: it is what Part 4 audits, and it keeps the
                // provenance question ("did this come from a link click or a bounce?") answerable
                // from the record alone.
                await _suppressionService.RecordAsync(
                    credential.Payload.Email,
                    SuppressionReason.ResidentRequest,
                    credential.CredentialType,
                    null,
                    null
                );
            }
            catch (ConcurrencyConflictException)
            {
                // Every retry lost its race - contention, not a fault. 409 preserves the old
                // WithOptimisticRetry contract: monitoring does not page for it, and a provider
                // driving RFC 8058 retries a non-2xx, which is exactly what this state needs.
                RecordPostCredentialRejection("concurrency-retries-exhausted");
                return Conflict(new { error = "Unable to save due to concurrent updates. Please try again." });
            }
            catch (Exception ex)
            {
                // Rethrown - a failed write must never answer 200. A mailbox provider driving RFC
                // 8058 records a success as honoured and stops offering the control, leaving the
                // resident with no way to make the mail stop; the 400/500 path at least makes the
                // provider retry. Named here first because the global handler's log carries no
                // identifier (the credential rides in the query string and is never logged).
                //
                // Not filtered on OperationCanceledException, same reasoning as the preferences
                // save below: this is a request path that rethrows either way, and the Cosmos
                // cancellation subtype is a real failed write that must not be silent.
                _logger.LogError(
                    ex,
                    "One-click unsubscribe failed to write a suppression for address {Email}",
                    SanitizeAddressForLog(EmailDeliveryActionService.RedactEmail(credential.Payload.Email))
                );
                throw;
            }

            // All-mail honest: the old per-category message described a boolean this endpoint no
            // longer writes.
            return Ok(new { message = "This address will no longer receive Canyon Oaks HOA email." });
        }

        /// <summary>
        /// Returns current email preferences for the token's home + email.
        /// </summary>
        [HttpGet("preferences")]
        public async Task<IActionResult> GetPreferences([FromQuery] string token, [FromQuery(Name = "u")] string linkId)
        {
            var credential = await ResolveCredentialAsync(token, linkId);
            if (credential == null)
                return BadRequest(new { error = "Invalid or missing credential." });
            var payload = credential.Payload;

            var (home, matchingAddresses, _, failure) = await LoadHomeAndMatchesAsync(payload);
            if (failure != null)
                return failure;

            // The suppression is what decides whether mail actually arrives, so the page has to
            // know about it: checkboxes whose changes silently have no effect are the same class
            // of trap as the false "Successfully unsubscribed" this controller already refuses to
            // emit. Cleared records read as no suppression - the booleans below are the whole
            // story again.
            var suppression = await _suppressionRepository.GetByEmailAsync(payload.Email);
            var suppressed = suppression != null && suppression.IsActive;

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
                Suppressed = suppressed,
                SuppressedUtc = suppressed ? suppression.SuppressedUtc : null,
                SuppressionReason = suppressed ? suppression.Reason.ToString() : null,
                CanResume = suppressed && suppression.Reason == Models.SuppressionReason.ResidentRequest,
            };

            return Ok(dto);
        }

        /// <summary>
        /// Lifts a <see cref="SuppressionReason.ResidentRequest"/> suppression - the "resume
        /// receiving email" action on the preferences page. Only that reason: undoing your own
        /// unsubscribe is symmetrical with making it (the same possession of the email authorises
        /// both), but a bounce or complaint record is evidence about deliverability, and lifting
        /// that is an admin decision reached via the board mailto.
        /// <para>
        /// Idempotent for the not-suppressed case: "make sure this address receives mail" is
        /// satisfied by there being nothing to lift, and a refresh-and-retry from the SPA must
        /// not turn into an error.
        /// </para>
        /// </summary>
        [HttpPost("preferences/resume")]
        public async Task<IActionResult> ResumeReceiving([FromQuery] string token, [FromQuery(Name = "u")] string linkId)
        {
            var credential = await ResolveCredentialAsync(token, linkId);
            if (credential == null)
                return BadRequest(new { error = "Invalid or missing credential." });

            // One call, no pre-read: the resident-clearable-only rule is enforced by the service
            // inside its own write cycle (onlyIfReason), so a bounce suppression that replaced a
            // ResidentRequest one between a page load and the click cannot be lifted here - a
            // check on this controller's earlier read would race exactly that window. ClearedBy
            // carries both who (a resident, self-service) and how (which credential shape proved
            // control of the address) - the same provenance rule as SuppressedBy.
            SuppressionClearOutcome outcome;
            try
            {
                outcome = await _suppressionService.ClearAsync(
                    credential.Payload.Email,
                    $"resident:{credential.CredentialType}",
                    onlyIfReason: Models.SuppressionReason.ResidentRequest
                );
            }
            catch (ConcurrencyConflictException)
            {
                RecordPostCredentialRejection("concurrency-retries-exhausted");
                return Conflict(new { error = "Unable to save due to concurrent updates. Please try again." });
            }

            // Still active after the call means the service refused: the in-force suppression is
            // not resident-clearable. Recorded so a run of these is visible - the SPA only offers
            // the button when the GET said CanResume, so this is a stale page or a hand-built
            // request; the reason value itself is never echoed.
            if (outcome.Suppression != null && outcome.Suppression.IsActive)
            {
                RecordPostCredentialRejection("suppression-not-resident-clearable");
                return BadRequest(
                    new { error = "Delivery to this address was stopped for a reason only an administrator can clear. Please contact board@cohad.org." }
                );
            }

            // Cleared by this call, cleared earlier, or never suppressed - all the same truthful
            // answer to "make sure this address receives mail".
            return Ok(new { message = "You will receive Canyon Oaks HOA email at this address again." });
        }

        /// <summary>
        /// Saves updated email preferences for the token's home + email.
        /// Only fields present in the request body are updated; omitted fields are left unchanged.
        /// </summary>
        [HttpPut("preferences")]
        public async Task<IActionResult> UpdatePreferences(
            [FromQuery] string token,
            [FromQuery(Name = "u")] string linkId,
            [FromBody] UpdateEmailPreferencesDto dto
        )
        {
            var credential = await ResolveCredentialAsync(token, linkId);
            if (credential == null)
                return BadRequest(new { error = "Invalid or missing credential." });
            var payload = credential.Payload;

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
        /// Resolves a presented credential, recording the outcome, and returns null when it is
        /// rejected. Callers get the full resolver result rather than the bare payload because the
        /// suppression writers stamp <c>CredentialType</c> as provenance (SuppressedBy/ClearedBy) -
        /// re-deriving it from the query string would be a second copy of the precedence rule.
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
        private async Task<UnsubscribeCredentialResult> ResolveCredentialAsync(string token, string linkId)
        {
            var result = await _credentialResolver.ResolveAsync(token, linkId);

            if (result.IsValid)
            {
                // The type comes from the resolver, which is the thing that decided which shape it
                // was actually resolving. Re-deriving it here from the query string would be a
                // second copy of the precedence rule, free to drift from the one that did the work.
                _logger.LogInformation(
                    "Unsubscribe credential accepted for {Operation} (type {CredentialType}).",
                    UnsubscribeDiagnostics.DescribeEndpoint(HttpContext),
                    result.CredentialType
                );
                return result;
            }

            // The credential itself is a bearer credential and is never recorded - only its length
            // and, when it is long enough that eight characters identify nothing, its sanitised
            // ends. Note a short link is 22 characters and so falls below that bar: its length
            // alone carries the diagnosis, which is the point of the threshold.
            RecordRejection(
                new UnsubscribeRejection
                {
                    // The shared rule, not a hard-coded TokenRejection: a bare request with no
                    // credential at all still reaches here, and billing that to the token stream
                    // would let tokenless crawler noise drain the budget protecting real
                    // mangled-link evidence.
                    Kind = UnsubscribeDiagnostics.ClassifyByTokenPresence(HttpContext),
                    // Reason is not set: the middleware logs Failure for credential rejections, and a
                    // second copy of the same value would be dead state nothing could catch drifting.
                    Failure = result.Failure,
                    // The value the resolver actually examined, not whichever parameter this method
                    // reached for first - otherwise a rejected short link would be logged with the
                    // length of an absent token.
                    TokenLength = result.PresentedValue?.Length ?? 0,
                    TokenEnds = DescribeTokenEnds(result.PresentedValue),
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
        /// The shortest credential whose first and last four characters may be logged. A legacy token
        /// is ~135 characters, so eight of them identify nothing. A short link id is 22 and the typed
        /// recovery code is nine, while head+tail is itself eight - so disclosing the ends of either
        /// would hand over most or nearly all of it. Below this length the ends are withheld and the
        /// logged length carries the diagnosis on its own, which is what it is there for.
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
        /// <para>
        /// Deliberately stricter than <see cref="SanitizeAddressForLog"/>. The shared invariant is
        /// only that nothing rendered into a log can contain a line break; the two alphabets differ
        /// because what each value must stay useful <em>as</em> differs, and collapsing everything
        /// outside base64url is right for an opaque credential and wrong for a readable address.
        /// </para>
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
        /// Neutralises line breaks and other control characters in an address before it is logged,
        /// leaving everything else - so the result stays a readable identifier an operator can match
        /// against a record, which is the entire reason it is logged.
        /// <para>
        /// The address reaches the log from the token payload, which is minted from a stored
        /// <c>EmailAddress.Address</c>, and storage accepts any value containing an '@'
        /// (<c>HomeController</c> validates no further). So a resident record saved with a CR/LF in
        /// its address would otherwise split the rendered line and let whoever entered it author
        /// what looks like a second, genuine entry - the same forging this file already guards
        /// against for tokens, reached through persisted data rather than a query string.
        /// </para>
        /// <para>
        /// U+2028 and U+2029 are included: they are not <see cref="char.IsControl(char)"/>, but
        /// several log viewers treat them as line terminators.
        /// </para>
        /// </summary>
        internal static string SanitizeAddressForLog(string value)
        {
            if (value == null)
                return null;

            return string.Create(
                value.Length,
                value,
                (destination, source) =>
                {
                    for (var i = 0; i < source.Length; i++)
                    {
                        var c = source[i];
                        destination[i] = char.IsControl(c) || c == '\u2028' || c == '\u2029' ? '.' : c;
                    }
                }
            );
        }

        /// <summary>
        /// Executes a read-modify-write on the home with optimistic concurrency retry.
        /// The action receives the freshly-loaded home and matching email addresses,
        /// applies modifications in place, and returns the IActionResult. If a
        /// concurrency conflict occurs, the entire cycle is retried.
        /// <para>
        /// The success response is returned only after <em>every</em> document the change touches
        /// has been written. The home and the residents are separate containers with no transaction
        /// between them, so a fault after the first write leaves a partial one, and a partial one is
        /// not a partial opt-out: recipient selection includes an address if <em>either</em> copy
        /// still opts in (<c>EmailController.GetAllEmailsMatchingFilter</c>), so mail keeps flowing
        /// until both are written. That state is recoverable - the caller is told the save failed,
        /// and the log names the home - whereas reporting success for it is not, because nothing
        /// downstream would ever revisit it.
        /// </para>
        /// <para>
        /// <b>Not covered here:</b> a resident document has no <c>ETag</c> and
        /// <c>CosmosResidentRepository.UpsertAsync</c> sends no precondition, so two writers who
        /// loaded the same resident still last-writer-wins silently. That is a data-layer gap rather
        /// than a control-flow one, and closing it means giving <c>Resident</c> optimistic
        /// concurrency across every path that writes one - see the design doc.
        /// </para>
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

                    // Only the residents that own a matched email address. These writes are part of
                    // the save, not a best-effort afterthought: the per-address opt-in booleans live
                    // on Resident documents, so a swallowed failure here answered "Successfully
                    // unsubscribed" for an opt-out that was never stored. A mailbox provider driving
                    // RFC 8058 one-click records that as honoured and stops offering the control, so
                    // the resident is left with no way to make the mail stop and no sign that
                    // anything went wrong - the exact reputation damage this endpoint exists to
                    // prevent. Failing is the honest answer: the SPA renders "Please try again", and
                    // the one-click path - where there is no human to retry - at least leaves an
                    // Error naming the home instead of a success nobody will ever revisit.
                    //
                    // Sequential rather than Task.WhenAll: this is one or two documents on a page a
                    // resident opens once, so there is no round-trip worth winning. Note that
                    // stopping at the first failure is not itself a virtue here - under the union
                    // semantics above, an address keeps receiving mail until *every* copy is
                    // written, so writing fewer of them on the way to a failure is neutral at best.
                    foreach (var resident in affectedResidents)
                        await _residentRepository.UpsertAsync(resident);

                    return result;
                }
                catch (ConcurrencyConflictException)
                {
                    // Fall through to the next attempt, or out of the loop on the last one. The
                    // conflict response lives in exactly one place below; an earlier revision also
                    // returned it from inside the catch, which made this one unreachable and left
                    // two copies of the same response to drift apart.
                    //
                    // Only the home write raises this today - a resident document carries no ETag,
                    // so its write cannot detect a lost race at all (see the remarks above). The
                    // catch sits outside both writes rather than around the home one because the
                    // rule is about the cycle, not about which container lost: re-reading and
                    // re-applying the whole change is what makes a retry safe either way, since an
                    // already-written home is simply written again with the same values.
                }
                catch (Exception ex)
                {
                    // Rethrown - the caller must still see the failure - but named here first. The
                    // global handler logs the fault itself, with a stack trace, so this is not the
                    // only trace; it is the only one that says *which record* to repair. The
                    // handler sees the method and path, and the path carries no identifier (the
                    // credential rides in the query string and is never logged).
                    //
                    // Home plus address is what a repair actually needs. The two documents disagree
                    // after a partial write, and comparing the home's copy of the address with the
                    // resident's shows exactly which flags failed to land - so the category does not
                    // have to be threaded down here to make the line actionable. The address is
                    // redacted with the same helper the delivery audit path uses, then sanitised:
                    // it originates in a stored record, and storage accepts any value containing an
                    // '@', so redaction alone would let a CR/LF saved in a resident record forge a
                    // second log entry.
                    //
                    // Deliberately NOT filtered on OperationCanceledException, against the usual
                    // repo rule. That rule exists so a BackgroundService shutdown is not logged as a
                    // failure; this is a request path that rethrows either way, so the filter buys
                    // nothing here. It would cost something, though: `CosmosOperationCanceledException`
                    // derives from `OperationCanceledException`, so the filter would discard the log
                    // for it. These repositories pass no cancellation token, so that is not the
                    // common fault - but an abandoned save leaves the same split state as any other,
                    // and silence is the one outcome this line exists to prevent.
                    _logger.LogError(
                        ex,
                        "Unsubscribe save failed for home {HomeId}, address {Email}; the change may be partially applied",
                        payload.HomeId,
                        SanitizeAddressForLog(EmailDeliveryActionService.RedactEmail(payload.Email))
                    );
                    throw;
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
