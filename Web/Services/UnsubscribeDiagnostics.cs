#nullable enable

// Nullable annotations are enabled for this file only: the Web project does not enable them
// globally, but these types encode a contract that is entirely about what is absent - a
// rejected token has no payload, a rejection recorded before the token was read has no
// reason or ends - and a signature that cannot say so invites the null-deref it documents.

using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Logging;

namespace Web.Services
{
    /// <summary>
    /// What the action knows about a rejection it is returning. The action records this; it does not
    /// log. <see cref="UnsubscribeDiagnosticsMiddleware"/> does the logging.
    /// </summary>
    public sealed class UnsubscribeRejection
    {
        /// <summary>Short, non-attacker-controlled classification, e.g. <c>home-not-found</c>.</summary>
        public string? Reason { get; init; }

        /// <summary>Which budget this draws on.</summary>
        public UnsubscribeWarningKind Kind { get; init; } = UnsubscribeWarningKind.TokenRejection;

        /// <summary>Set only when a token was presented and rejected.</summary>
        public UnsubscribeTokenFailure? Failure { get; init; }

        /// <summary>Length of the presented token, when there was one.</summary>
        public int? TokenLength { get; init; }

        /// <summary>Redacted ends of the presented token, when there was one.</summary>
        public string? TokenEnds { get; init; }
    }

    /// <summary>Records a rejection on the current request for the diagnostics middleware to log.</summary>
    public static class UnsubscribeDiagnostics
    {
        /// <summary>
        /// Which kind of credential the caller presented. Logged on every resolution so legacy
        /// redemptions are countable: legacy token support is removed when this reaches zero and
        /// holds, not on a calendar date. Defined once - the acceptance log and the rejection log
        /// must agree or the counter that decides retirement is split across two values. See
        /// <c>docs/email-suppression-and-unsubscribe.md</c>.
        /// </summary>
        public const string LegacyTokenCredential = "LegacyToken";

        /// <summary>
        /// Credential type for a request that presented none. Distinct from
        /// <see cref="LegacyTokenCredential"/> because the retirement decision counts legacy
        /// redemptions: labelling tokenless crawler traffic as a legacy token would keep that count
        /// permanently above zero and legacy support would never be retired on evidence.
        /// </summary>
        public const string NoCredential = "None";

        /// <summary>
        /// Credential type for the short <c>/u/{id}</c> link. A new value in an existing log
        /// dimension rather than a new dimension, so the retirement counter is a single query
        /// grouping by type: legacy support goes when <see cref="LegacyTokenCredential"/> reaches
        /// zero and this one carries the traffic.
        /// </summary>
        public const string ShortLinkCredential = "ShortLink";

        /// <summary>The unsubscribe route prefix and the stable label logged for it.</summary>
        internal readonly record struct UnsubscribeRoute(string Prefix, string Label);

        /// <summary>
        /// The unsubscribe routes and their log labels, defined once so a new entry cannot be
        /// matched without also being nameable. Matched by path only when no action was selected,
        /// because routing can reject a request before choosing one. <c>EmailController</c> is
        /// <c>[Route("api/[controller]")]</c>, which resolves to the same <c>/api/email</c> prefix
        /// case-insensitively, so these must stay specific to the unsubscribe segments rather than
        /// matching the prefix - otherwise authenticated email-admin failures would be logged here
        /// and would consume this budget.
        /// </summary>
        internal static readonly UnsubscribeRoute[] UnsubscribeRoutes =
        [
            new("/api/email/preferences", "preferences"),
            new("/api/email/unsubscribe", "one-click-unsubscribe"),
        ];

        /// <summary>The query parameter carrying the legacy credential.</summary>
        internal const string TokenParameter = "token";

        /// <summary>
        /// The query parameter carrying the short link id. The body link puts the id in a path
        /// segment (<c>/u/{id}</c>) and the SPA forwards it here; the <c>List-Unsubscribe</c> header
        /// URL uses this parameter directly.
        /// <para>
        /// Deliberately <c>u</c> and not <c>id</c>. <see cref="ClassifyByTokenPresence"/> bills any
        /// request carrying a credential parameter to the credential warning budget, and <c>id</c>
        /// is one of the most common query names on the web - so on an <c>[AllowAnonymous]</c>
        /// endpoint a crawler walking <c>?id=1..N</c> would drain the budget that protects the
        /// stripped-link evidence, which is the exact outcome the two-budget split exists to
        /// prevent. It would inflate the retirement counter with the same traffic. A parameter name
        /// nobody guesses by accident costs nothing and removes the whole class.
        /// </para>
        /// </summary>
        internal const string LinkIdParameter = "u";

        /// <summary>
        /// Every parameter that can carry a credential, in the resolver's precedence order. Defined
        /// once so presence tests and the resolver cannot disagree about what counts as "a
        /// credential was presented" - a disagreement there would misfile rejections into the budget
        /// an anonymous flood can drain.
        /// </summary>
        internal static readonly string[] CredentialParameters = [LinkIdParameter, TokenParameter];

        /// <summary>Label for a request that matched no unsubscribe route.</summary>
        internal const string UnknownEndpoint = "unknown";

        /// <summary>
        /// Names the endpoint without echoing the URL, whose category segment is attacker-controlled.
        /// <para>
        /// Derived from the <em>path</em>, never from the MVC action name. Routing rejects some
        /// requests before choosing an action, so an action-name label would give the same endpoint
        /// two different values depending on how far the request got - and an operator filtering on
        /// one would silently miss the other half of the same incident.
        /// </para>
        /// </summary>
        public static string DescribeEndpoint(HttpContext context)
        {
            foreach (var route in UnsubscribeRoutes)
            {
                if (context.Request.Path.StartsWithSegments(route.Prefix, StringComparison.OrdinalIgnoreCase))
                    return route.Label;
            }

            return UnknownEndpoint;
        }

        /// <summary>
        /// Whether a credential was actually supplied, as opposed to whether the parameter appeared.
        /// A value is required: `?token=` carries the key but no credential.
        /// <para>
        /// Distinct from <see cref="ClassifyByTokenPresence"/> on purpose - see the note at its call
        /// site in the middleware. This one feeds the legacy-redemption counter, which must not be
        /// inflated by stripped links, and it must agree with the acceptance log for that count to
        /// mean anything.
        /// </para>
        /// </summary>
        /// <remarks>
        /// Precedence matches <see cref="UnsubscribeCredentialResolver"/>: the short link wins when
        /// both parameters carry a value, so the type logged names the credential that was actually
        /// resolved. The resolver reports its own type on every outcome and the controller prefers
        /// that; this remains the fallback for rejections produced before the action ran, where
        /// there is no resolver result to ask.
        /// </remarks>
        public static string DescribeCredentialType(HttpContext context)
        {
            if (!string.IsNullOrWhiteSpace(context.Request.Query[LinkIdParameter]))
                return ShortLinkCredential;

            return string.IsNullOrWhiteSpace(context.Request.Query[TokenParameter])
                ? NoCredential
                : LegacyTokenCredential;
        }

        /// <summary>
        /// The single rule for which budget a rejection draws on. Every site that classifies one
        /// calls this - the controller when it records a rejection, and the middleware as the
        /// fallback when the action never ran to record anything. A new rejection path should call
        /// it rather than hard-code a <see cref="UnsubscribeWarningKind"/>: a tokenless request
        /// billed to the token stream lets an anonymous flood suppress the stripped-link evidence
        /// this feature exists to surface.
        /// <para>
        /// A credential parameter being <em>present</em> is what moves a rejection into the token
        /// stream, deliberately including a stripped <c>?token=</c> or <c>?id=</c> - that is the
        /// evidence worth protecting, and under short links a stripped <c>?id=</c> is now the
        /// likeliest shape of it. Contrast <see cref="DescribeCredentialType"/>, which requires a
        /// value because it feeds the legacy-redemption counter rather than the budget.
        /// </para>
        /// <para>
        /// The name says "token" and the <see cref="UnsubscribeWarningKind"/> values still do too,
        /// while both now cover either shape. That is deliberate: <c>WarningKind</c> is an emitted
        /// log dimension, operators are querying it against a live incident, and renaming the values
        /// would silently break those queries to buy nothing an operator can see. The vocabulary
        /// here is the cost of not moving an external contract mid-investigation.
        /// </para>
        /// </summary>
        public static UnsubscribeWarningKind ClassifyByTokenPresence(HttpContext context)
        {
            foreach (var parameter in CredentialParameters)
            {
                if (context.Request.Query.ContainsKey(parameter))
                    return UnsubscribeWarningKind.TokenRejection;
            }

            return UnsubscribeWarningKind.PreTokenRejection;
        }

        private const string ItemKey = "Unsubscribe.Rejection";

        public static void Record(HttpContext httpContext, UnsubscribeRejection rejection)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            ArgumentNullException.ThrowIfNull(rejection);
            httpContext.Items[ItemKey] = rejection;
        }

        public static UnsubscribeRejection? Get(HttpContext? httpContext) =>
            httpContext?.Items.TryGetValue(ItemKey, out var value) == true ? value as UnsubscribeRejection : null;
    }

    /// <summary>
    /// Logs the failing <b>4xx</b> responses from the unsubscribe endpoints, in one place.
    /// <para>
    /// Not 5xx: <c>UseExceptionHandler</c> is registered before <c>UseRouting</c> and therefore
    /// outside this middleware, so an exception unwinds past <c>await _next</c> without reaching the
    /// code below. That is deliberate - the global handler already logs unhandled exceptions with a
    /// stack trace, and duplicating them here would consume this budget for failures that are not
    /// rejections.
    /// </para>
    /// <para>
    /// This is middleware, and not logging in the action, because rejections are produced at three
    /// different layers and only the outermost sees them all. Logging in the action body produced
    /// four separate defects: the automatic <c>400</c> for an unparseable body comes from
    /// <c>[ApiController]</c>'s model-state filter, before the action; the <c>415</c> for a wrong
    /// content type is produced by <b>routing</b>, which matches a synthetic
    /// "415 HTTP Unsupported Media Type" endpoint so the MVC filter pipeline never runs at all; and
    /// two branches tried to distinguish an absent parameter from an emptied one after model binding
    /// had already collapsed both to null. An action cannot observe what is rejected ahead of it,
    /// and unit tests that invoke the action directly cannot observe that it cannot - which is why
    /// <c>UnsubscribeDiagnosticsPipelineTests</c> drives a real pipeline.
    /// </para>
    /// <para>
    /// A result filter is not enough: an <c>IAlwaysRunResultFilter</c> catches the model-state 400
    /// but never runs for the routing-level 415. Middleware sits outside all of it and simply reads
    /// the status code that was actually sent. Rejections the action understands in detail arrive via
    /// <see cref="UnsubscribeDiagnostics.Record"/>; anything else is logged from the status alone,
    /// which still beats the silence that made the July 2026 incident undiagnosable.
    /// </para>
    /// </summary>
    public sealed class UnsubscribeDiagnosticsMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly IUnsubscribeWarningBudget _budget;
        private readonly ILogger<UnsubscribeDiagnosticsMiddleware> _logger;

        // Both dependencies are singletons, so they are taken here rather than as InvokeAsync
        // parameters: those are resolved from the request container on every request the site
        // serves, not just the two paths this acts on.
        public UnsubscribeDiagnosticsMiddleware(
            RequestDelegate next,
            IUnsubscribeWarningBudget budget,
            ILogger<UnsubscribeDiagnosticsMiddleware> logger
        )
        {
            _next = next;
            _budget = budget;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            await _next(context);

            // 4xx only, matching the documented contract. A 5xx returned rather than thrown is a
            // fault, not a rejection: logging it here would describe it as one and would spend the
            // budget that protects the rejection evidence.
            var statusCode = context.Response.StatusCode;
            if (statusCode is < 400 or >= 500 || !IsUnsubscribeRequest(context))
                return;

            try
            {
                LogRejection(context, _budget, _logger);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The response has already been written by this point, so a fault in the diagnostics
                // must not become the caller's problem: failing to describe a rejection is bad, but
                // turning a clean 400 into a torn response is worse. A mismatched log template threw
                // here once already.
                _logger.LogError(ex, "Failed to log an unsubscribe rejection.");
            }
        }

        private static void LogRejection(HttpContext context, IUnsubscribeWarningBudget budget, ILogger logger)
        {
            var rejection = UnsubscribeDiagnostics.Get(context);

            // No record means the action never ran to make one. Classify on the same rule the
            // action uses: a request that presented a token belongs to the token stream even when
            // it was turned away before the token could be read. A provider changing its content
            // type produces a routing-level 415 and is exactly as worth catching as one changing
            // its body - billing it to the budget an empty POST can flood would hide it.
            var kind = rejection?.Kind ?? UnsubscribeDiagnostics.ClassifyByTokenPresence(context);

            var decision = budget.Consume(kind);
            if (decision == UnsubscribeWarningDecision.Suppressed)
                return;

            var operation = UnsubscribeDiagnostics.DescribeEndpoint(context);
            var statusCode = context.Response.StatusCode;

            // Deliberately NOT derived from `kind`. The budget asks "did this look like it came
            // from an unsubscribe link", so it keys on the parameter being *present* - a stripped
            // `?token=` must draw on the token budget, because that is the evidence worth
            // protecting. CredentialType asks a different question, "was a credential actually
            // supplied", and the two answers diverge on exactly that input: `?token=` has the key
            // and no credential. Deriving one from the other logged a stripped link as a rejected
            // legacy token with `Token length 0`, holding the retirement counter above zero for the
            // very traffic this feature exists to surface.
            var credentialType = UnsubscribeDiagnostics.DescribeCredentialType(context);

            try
            {
                if (rejection?.Failure is not null)
                {
                    // {WarningKind} is the only thing separating a stripped `?token=` from a request
                    // that carried no token at all: model binding collapses both to null, so Failure,
                    // TokenLength and TokenEnds are identical for the two. Computing the distinction
                    // and then not logging it would leave the mangled-link evidence indistinguishable
                    // from crawler noise, which is the diagnosis this whole change exists to enable.
                    //
                    // The token is a bearer credential and is never logged - only its length and, for
                    // tokens long enough that it identifies nothing, its sanitised ends.
                    logger.LogWarning(
                        "Unsubscribe credential rejected for {Operation} ({WarningKind}, type {CredentialType}) with status {StatusCode}: {Reason}. Token length {TokenLength}, ends {TokenEnds}.",
                        operation,
                        kind,
                        credentialType,
                        statusCode,
                        rejection.Failure,
                        rejection.TokenLength ?? 0,
                        rejection.TokenEnds
                    );
                }
                else
                {
                    // Same dimensions as the branch above - Operation, WarningKind, CredentialType,
                    // StatusCode, Reason - so one query covers both halves of an incident.
                    logger.LogWarning(
                        "Unsubscribe request rejected for {Operation} ({WarningKind}, type {CredentialType}) with status {StatusCode}: {Reason}.",
                        operation,
                        kind,
                        credentialType,
                        statusCode,
                        rejection?.Reason ?? DescribeStatus(statusCode)
                    );
                }
            }
            finally
            {
                // The notice is what makes the ensuing silence attributable, so losing it to a fault
                // in the rejection log above would leave an operator unable to tell a spent budget
                // from a quiet endpoint - hence the finally.
                //
                // Its own failure is swallowed, though: throwing out of a finally while an exception
                // is already in flight *replaces* that exception, so a broken announcement template
                // would erase the record of the broken rejection template - the more important of
                // the two, and the one InvokeAsync's catch exists to name. It was an announcement
                // template that threw during development, so this is not hypothetical.
                try
                {
                    AnnounceIfBudgetSpent(kind, decision, logger);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Deliberately not rethrown or logged through the same logger that just failed.
                    System.Diagnostics.Debug.WriteLine(ex);
                }
            }
        }

        private static bool IsUnsubscribeRequest(HttpContext context)
        {
            var descriptor = context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>();
            if (descriptor != null)
                // `global::` and not merely `Web.Controllers.`: inside `namespace Web.Services` even
                // a leading `Web` is resolved by walking enclosing scopes first, so a future
                // `Web.Services.Web` or `Web.Services.Controllers` would silently rebind this to a
                // different type. The middleware would then go quiet for every unsubscribe request
                // with nothing failing to say so, which is the one outcome this file exists to
                // prevent. Only the global alias is immune.
                return descriptor.ControllerTypeInfo.AsType() == typeof(global::Web.Controllers.UnsubscribeController);

            // One matcher: the label lookup already walks the route table, and "unknown" is exactly
            // "no route matched". A second copy of the rule is what the route table replaced.
            return UnsubscribeDiagnostics.DescribeEndpoint(context) != UnsubscribeDiagnostics.UnknownEndpoint;
        }

        /// <summary>
        /// Names a failure produced before any action could classify it. These are the cases that
        /// previously vanished entirely.
        /// </summary>
        private static string DescribeStatus(int statusCode) =>
            statusCode switch
            {
                400 => "rejected-before-action-model-binding",
                405 => "rejected-before-action-method-not-allowed",
                415 => "rejected-before-action-unsupported-media-type",
                _ => "rejected-before-action",
            };

        /// <summary>
        /// Announces that a budget is spent, so the silence that follows is attributable. The kind is
        /// named because the budgets are per-kind: a bare "warnings are suppressed" would let an
        /// operator conclude that credential warnings had stopped when only the unauthenticated
        /// stream had.
        /// </summary>
        private static void AnnounceIfBudgetSpent(
            UnsubscribeWarningKind kind,
            UnsubscribeWarningDecision decision,
            ILogger logger
        )
        {
            if (decision != UnsubscribeWarningDecision.LastAllowed)
                return;

            // One placeholder per argument. Repeating a name creates a second positional slot, not a
            // second reference to the same value, and the formatter throws on the missing argument.
            logger.LogWarning(
                "Unsubscribe {WarningKind} warnings have reached the cap of {Cap} per {WindowHours}h and are suppressed until that window rolls over. Other kinds are unaffected.",
                kind,
                UnsubscribeWarningBudget.MaxWarningsPerWindow,
                UnsubscribeWarningBudget.Window.TotalHours
            );
        }
    }
}
