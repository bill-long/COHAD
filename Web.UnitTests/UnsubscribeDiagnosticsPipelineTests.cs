using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Web.Controllers;
using Web.MockData;
using Web.Models;
using Web.Services;
using Web.Services.Repositories;

namespace Web.UnitTests
{
    /// <summary>
    /// Exercises the unsubscribe endpoints through a real MVC pipeline.
    /// <para>
    /// These exist because four separate defects in this feature were invisible to tests that call
    /// the action method directly: model binding collapses an empty <c>?token=</c> and an empty form
    /// field to null, <c>[Consumes]</c> rejects a wrong content type with 415 before the action runs,
    /// and <c>[ApiController]</c> rejects an unparseable body with 400 before the action runs. A test
    /// that bypasses the pipeline cannot observe any of that, and two such tests passed green while
    /// production did the opposite. Anything asserting what is or is not logged for a *rejection*
    /// belongs here, not in the direct-invocation tests.
    /// </para>
    /// <para>
    /// What this harness does <b>not</b> establish: it builds its own pipeline
    /// (<c>UseExceptionHandler</c> -> <c>UseRouting</c> -> this middleware -> <c>UseEndpoints</c>)
    /// rather than running <c>Startup</c>, so it cannot catch a regression in production's
    /// middleware <em>ordering</em>. The exception handler is placed outside the middleware as
    /// production places it - that ordering is what makes 5xx invisible here - but nothing ties the
    /// two together. Production additionally has auth between this middleware and the endpoints, and
    /// <c>UseSpa</c>'s catch-all that turns an unmatched unsubscribe URL into a 200. Moving
    /// <c>app.UseMiddleware&lt;UnsubscribeDiagnosticsMiddleware&gt;()</c> after <c>UseEndpoints</c>
    /// would change production behaviour while every test here still passed.
    /// </para>
    /// </summary>
    public class UnsubscribeDiagnosticsPipelineTests : IDisposable
    {
        private readonly Mock<IUnsubscribeTokenService> _tokenService = new();
        private readonly MockUnsubscribeLinkRepository _linkRepository = new();
        private readonly Mock<IHomeRepository> _homeRepository = new();
        private readonly Mock<IResidentRepository> _residentRepository = new();
        private readonly RecordingLoggerProvider _logs = new();
        private readonly IHost _host;
        private readonly HttpClient _client;

        public UnsubscribeDiagnosticsPipelineTests()
        {
            _host = new HostBuilder()
                .ConfigureWebHost(web =>
                {
                    web.UseTestServer()
                        .ConfigureServices(services =>
                        {
                            services.AddLogging(b =>
                            {
                                b.ClearProviders();
                                b.SetMinimumLevel(LogLevel.Trace);
                                b.AddProvider(_logs);
                            });

                            services.AddSingleton(_tokenService.Object);
                            services.AddSingleton(_homeRepository.Object);
                            services.AddSingleton(_residentRepository.Object);
                            services.AddSingleton(TimeProvider.System);
                            services.AddSingleton<IUnsubscribeWarningBudget, UnsubscribeWarningBudget>();

                            // The real resolver, not a mock: this harness exists to observe what the
                            // pipeline does to a request, and a stubbed resolver would decide the
                            // credential type and failure reason that half these assertions are about.
                            services.AddSingleton<IUnsubscribeLinkRepository>(_linkRepository);
                            services.AddSingleton<IUnsubscribeCredentialResolver, UnsubscribeCredentialResolver>();

                            // Only this controller - the rest of the app's controllers would drag in
                            // dependencies irrelevant to the pipeline behaviour under test.
                            services
                                .AddControllers()
                                // The test assembly is the entry assembly, so Web.dll is not an
                                // application part by default and no controller would be found.
                                .AddApplicationPart(typeof(UnsubscribeController).Assembly)
                                .ConfigureApplicationPartManager(parts =>
                                {
                                    foreach (
                                        var existing in parts
                                            .FeatureProviders.OfType<ControllerFeatureProvider>()
                                            .ToList()
                                    )
                                    {
                                        parts.FeatureProviders.Remove(existing);
                                    }

                                    parts.FeatureProviders.Add(new OnlyUnsubscribeController());
                                });
                        })
                        .Configure(app =>
                        {
                            // Production registers UseExceptionHandler before UseRouting, and that
                            // ordering is the basis for the middleware observing 4xx only. Mirrored
                            // here so a fault inside an action is answered with the same 500 a
                            // caller would receive, rather than surfacing as a thrown exception at
                            // the test client - which would hide what the caller actually sees.
                            //
                            // It mirrors the status code only. Production's handler also logs the
                            // fault at Error (Startup.cs), and nothing here locks that; asserting a
                            // stand-in's own logging would prove nothing about Startup. What the
                            // suite does lock is the controller's Error line naming the home, which
                            // is the evidence a half-applied opt-out actually needs.
                            //
                            // A consequence worth knowing: a throwing action no longer fails a test
                            // by escaping the client call. Tests that assert the *absence* of
                            // something must anchor on the expected status first or they can pass
                            // for the wrong reason.
                            app.UseExceptionHandler(errorApp =>
                                errorApp.Run(async context =>
                                {
                                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                                    context.Response.ContentType = "application/json";
                                    await context.Response.WriteAsync("{\"error\":\"An unexpected error occurred.\"}");
                                })
                            );
                            app.UseRouting();
                            app.UseMiddleware<UnsubscribeDiagnosticsMiddleware>();
                            app.UseEndpoints(e => e.MapControllers());
                        });
                })
                .Start();

            _client = _host.GetTestClient();
        }

        public void Dispose()
        {
            _client.Dispose();
            _host.Dispose();
            GC.SuppressFinalize(this);
        }

        private sealed class OnlyUnsubscribeController : ControllerFeatureProvider
        {
            protected override bool IsController(System.Reflection.TypeInfo typeInfo) =>
                typeInfo.AsType() == typeof(UnsubscribeController);
        }

        private static UnsubscribeTokenResult Rejected(UnsubscribeTokenFailure failure) =>
            UnsubscribeTokenResult.Failed(failure);

        private static UnsubscribeTokenResult Valid(Guid homeId, string email) =>
            UnsubscribeTokenResult.Success(new UnsubscribeTokenPayload { HomeId = homeId, Email = email });

        /// <summary>
        /// Only this app's own log entries. The framework's request logging renders the full URL,
        /// query string included, at Information - that is not our code disclosing the token, and in
        /// production the app-wide filter is Warning so those records are never emitted at all.
        /// </summary>
        private IReadOnlyList<RecordedLog> Ours =>
            _logs.Entries.Where(e => e.Category.StartsWith("Web.", StringComparison.Ordinal)).ToList();

        private IReadOnlyList<RecordedLog> Warnings => Ours.Where(e => e.Level == LogLevel.Warning).ToList();

        // --- Failures the pipeline produces before the action runs ---

        [Fact]
        public async Task UnsupportedMediaType_IsLoggedEvenThoughTheActionNeverRuns()
        {
            // Routing - not MVC - produces this: it matches a synthetic "415 HTTP Unsupported Media
            // Type" endpoint and the filter pipeline never runs. Neither an action nor any result
            // filter can see it, which is why the diagnostics are middleware.
            var response = await _client.PostAsync(
                "/api/email/unsubscribe/board?token=abc",
                new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
            );

            Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
            Assert.Contains(Warnings, w => w.Message.Contains("unsupported-media-type"));
        }

        [Fact]
        public async Task UnparseableJsonBody_IsLoggedEvenThoughTheActionNeverRuns()
        {
            // [ApiController]'s model-state filter returns 400 before the action body executes.
            _tokenService.Setup(s => s.ValidateToken(It.IsAny<string>())).Returns(Valid(Guid.NewGuid(), "j@x.com"));

            var response = await _client.PutAsync(
                "/api/email/preferences?token=abc",
                new StringContent("this is not json", System.Text.Encoding.UTF8, "application/json")
            );

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains(Warnings, w => w.Message.Contains("rejected-before-action"));
        }

        // --- Locks on the controller's non-annotated nullability context ---
        //
        // `#nullable enable` on an MVC action is binding semantics, not documentation: annotating
        // the parameters made `category` implicitly required and flipped `[FromBody]`'s
        // EmptyBodyBehavior to Allow. Each test below names which half it actually pins, because an
        // earlier version of this block claimed both and one of them passed for an unrelated reason.

        [Fact]
        public async Task NoContentType_IsRejectedBeforeTheActionAsUnsupportedMediaType()
        {
            // Independent of nullability: with no Content-Type the body binder finds no input
            // formatter and returns 415 before EmptyBodyBehavior is ever consulted. Kept because the
            // 415 classification is worth locking, NOT because it detects an annotation change.
            _tokenService.Setup(s => s.ValidateToken(It.IsAny<string>())).Returns(Valid(Guid.NewGuid(), "j@x.com"));

            var response = await _client.PutAsync("/api/email/preferences?token=abc", content: null);

            Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
            Assert.Contains(Warnings, w => w.Message.Contains("unsupported-media-type"));
        }

        [Fact]
        public async Task EmptyJsonBody_IsRejectedBeforeTheActionRatherThanReachingIt()
        {
            // This is the EmptyBodyBehavior lock. A well-formed request with an empty JSON body is
            // turned away by model binding today. Make the body parameter nullable and the action
            // runs instead, logging "missing-request-body" and erasing the pre-action signal.
            _tokenService.Setup(s => s.ValidateToken(It.IsAny<string>())).Returns(Valid(Guid.NewGuid(), "j@x.com"));

            var response = await _client.PutAsync(
                "/api/email/preferences?token=abc",
                new StringContent("", System.Text.Encoding.UTF8, "application/json")
            );

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains(Warnings, w => w.Message.Contains("rejected-before-action"));
            Assert.DoesNotContain(Warnings, w => w.Message.Contains("missing-request-body"));
        }

        [Fact]
        public async Task WhitespaceCategory_StillReachesTheActionAndIsClassified()
        {
            // This is the implicit-[Required] lock. A category segment mangled to whitespace in
            // transit is exactly the failure class this PR exists to diagnose; annotate the
            // controller and model binding rejects it before the action, losing the classification.
            var homeId = Guid.NewGuid();
            _tokenService.Setup(s => s.ValidateToken(It.IsAny<string>())).Returns(Valid(homeId, "j@x.com"));

            var response = await _client.PostAsync(
                "/api/email/unsubscribe/%20?token=tok",
                new StringContent(
                    "List-Unsubscribe=One-Click",
                    System.Text.Encoding.UTF8,
                    "application/x-www-form-urlencoded"
                )
            );

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains(Warnings, w => w.Message.Contains("unknown-category"));
        }

        // --- Distinctions model binding erases ---

        [Fact]
        public async Task EmptyTokenQueryValue_IsStillLoggedAsARejection()
        {
            // `?token=` binds to null exactly like an absent parameter. It must not be silent: this
            // is what a link stripped in transit looks like.
            _tokenService
                .Setup(s => s.ValidateToken(It.IsAny<string>()))
                .Returns(Rejected(UnsubscribeTokenFailure.Missing));

            var response = await _client.GetAsync("/api/email/preferences?token=");

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains(Warnings, w => w.Message.Contains("Missing"));
        }

        [Theory]
        [InlineData("List-Unsubscribe=", "confirmation-present-but-not-one-click")]
        [InlineData("Other=x", "confirmation-field-absent")]
        public async Task EmptyAndAbsentConfirmationFieldsAreDistinguished(string body, string expectedReason)
        {
            // Form binding collapses an empty value to null, so the bound string cannot tell these
            // apart; the form collection still can, and the difference is exactly the mangling
            // question this work is about.
            var response = await _client.PostAsync(
                "/api/email/unsubscribe/board?token=abc",
                new StringContent(body, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded")
            );

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains(Warnings, w => w.Message.Contains(expectedReason));
        }

        // --- Rejections the action classifies ---

        [Fact]
        public async Task RejectedToken_LogsReasonAndShapeButNeverTheToken()
        {
            const string secret = "abcdefghijklmnopqrstuvwxyz0123456789ABCDEF";
            _tokenService
                .Setup(s => s.ValidateToken(secret))
                .Returns(Rejected(UnsubscribeTokenFailure.MalformedBase64));

            var response = await _client.GetAsync($"/api/email/preferences?token={secret}");

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains(Warnings, w => w.Message.Contains("MalformedBase64") && w.Message.Contains("42"));
            Assert.DoesNotContain(Ours, e => e.Message.Contains(secret));
        }

        [Fact]
        public async Task HomeNotFound_LogsRatherThanReturning404Silently()
        {
            var homeId = Guid.NewGuid();
            _tokenService.Setup(s => s.ValidateToken("tok")).Returns(Valid(homeId, "j@x.com"));
            _homeRepository.Setup(r => r.GetByIdAsync(homeId)).ReturnsAsync((Home?)null);
            _residentRepository.Setup(r => r.GetByHomeIdAsync(homeId)).ReturnsAsync(new List<Resident>());

            var response = await _client.GetAsync("/api/email/preferences?token=tok");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Contains(Warnings, w => w.Message.Contains("home-not-found"));
        }

        [Fact]
        public async Task SuccessfulRequest_LogsNoWarning()
        {
            var homeId = Guid.NewGuid();
            var home = new Home
            {
                Id = homeId,
                StreetNumber = 123,
                StreetName = "Oak Avenue",
                EmailAddress = new EmailAddress { Address = "j@x.com", BoardEmailOptedIn = true },
            };
            _tokenService.Setup(s => s.ValidateToken("tok")).Returns(Valid(homeId, "j@x.com"));
            _homeRepository.Setup(r => r.GetByIdAsync(homeId)).ReturnsAsync(home);
            _residentRepository.Setup(r => r.GetByHomeIdAsync(homeId)).ReturnsAsync(new List<Resident>());

            var response = await _client.GetAsync("/api/email/preferences?token=tok");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Empty(Warnings);
        }

        // --- A save that did not persist must not be reported as a success ---

        [Fact]
        public async Task OneClickUnsubscribe_WhenTheResidentSaveFails_TheCallerIsNotToldItSucceeded()
        {
            // The per-address opt-in booleans live on Resident documents, so a failed resident write
            // means the opt-out was not stored. Reporting 200 for it is worse than reporting the
            // failure: Gmail records a one-click as honoured and stops offering the control, so the
            // resident is left with no way to make the mail stop and no sign that anything went
            // wrong. Asserted end to end because the response text is the thing that lied.
            var homeId = ArrangeAFailingResidentSave();

            var response = await OneClickUnsubscribeAsync();

            // Not a body assertion: the harness writes the 500 body itself, so checking it for the
            // absence of the success text could not fail. The status is the whole contract.
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

            // The home write already landed, so this request leaves a half-applied opt-out and the
            // resident keeps receiving mail. Nothing else names the record: the middleware skips 5xx
            // by design and the global handler logs only the method and path. Without this line the
            // fix would trade a false success for an unrepairable one - so the home id and the
            // redacted address are both asserted, since either alone leaves the operator guessing.
            Assert.Contains(
                Ours,
                e =>
                    e.Level == LogLevel.Error
                    && e.Message.Contains(homeId.ToString())
                    && e.Message.Contains("jan***@example.com")
            );
        }

        [Fact]
        public async Task AFailedSaveIsNeitherLoggedAsARejectionNorBilledToTheBudget()
        {
            // A save that faults must not be able to drain the budget: during a Cosmos outage every
            // unsubscribe attempt fails, and if those spent the window the mangled-link evidence the
            // budget exists to protect would be suppressed exactly when the endpoint is loudest.
            //
            // Two things keep that from happening, and it is worth being precise about which does
            // the work here. The failure is *thrown*, so it unwinds past `await _next` and the
            // middleware's post-processing never runs at all - the `>= 500` carve-out is the second
            // line of defence, for a 5xx *returned* as a result, and this test does not reach it.
            // What is asserted is the outcome the two produce together, which had no test before.
            //
            // The loop is what makes that an assertion rather than a hope: checking only that the
            // faults logged no warning cannot tell an unspent budget from a suppressed one. Driving
            // a whole window's worth and then a genuine rejection can.
            ArrangeAFailingResidentSave();

            for (var i = 0; i < UnsubscribeWarningBudget.MaxWarningsPerWindow; i++)
            {
                var failure = await OneClickUnsubscribeAsync();
                Assert.Equal(HttpStatusCode.InternalServerError, failure.StatusCode);
            }

            Assert.Empty(Warnings);

            _tokenService
                .Setup(s => s.ValidateToken("bad"))
                .Returns(Rejected(UnsubscribeTokenFailure.DecryptFailed));
            var rejected = await _client.GetAsync("/api/email/preferences?token=bad");

            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.Contains(Warnings, w => w.Message.Contains("DecryptFailed"));
        }

        /// <summary>
        /// Arranges the half-applied case: the address is on <b>both</b> the home and the resident,
        /// and only the resident write fails. The home copy really is opted out and the resident
        /// copy really is not, which is the split state the Error log exists to make repairable.
        /// An earlier version gave the home a different address, so the home write persisted an
        /// unchanged document and the opt-out was wholly unapplied rather than half-applied - the
        /// fixture did not build the state its own comments described.
        /// </summary>
        private Guid ArrangeAFailingResidentSave()
        {
            var homeId = Guid.NewGuid();
            const string email = "jane@example.com";
            var home = new Home
            {
                Id = homeId,
                StreetNumber = 123,
                StreetName = "Oak Avenue",
                EmailAddress = new EmailAddress { Address = email, BoardEmailOptedIn = true },
            };
            var resident = new Resident
            {
                Id = Guid.NewGuid(),
                HomeId = homeId,
                EmailAddresses = new List<EmailAddress>
                {
                    new EmailAddress { Address = email, BoardEmailOptedIn = true },
                },
            };

            _tokenService.Setup(s => s.ValidateToken("tok")).Returns(Valid(homeId, email));
            _homeRepository.Setup(r => r.GetByIdAsync(homeId)).ReturnsAsync(home);
            _homeRepository.Setup(r => r.UpsertAsync(It.IsAny<Home>())).ReturnsAsync(home);
            _residentRepository.Setup(r => r.GetByHomeIdAsync(homeId)).ReturnsAsync(new List<Resident> { resident });
            _residentRepository
                .Setup(r => r.UpsertAsync(It.IsAny<Resident>()))
                .ThrowsAsync(new InvalidOperationException("Cosmos is unavailable."));

            return homeId;
        }

        private Task<HttpResponseMessage> OneClickUnsubscribeAsync() =>
            _client.PostAsync(
                "/api/email/unsubscribe/board?token=tok",
                new StringContent(
                    "List-Unsubscribe=One-Click",
                    System.Text.Encoding.UTF8,
                    "application/x-www-form-urlencoded"
                )
            );

        // --- Budget ---

        [Fact]
        public async Task WarningsStopAtTheCapAndTheLastOneSaysWhichKindIsSuppressed()
        {
            _tokenService
                .Setup(s => s.ValidateToken(It.IsAny<string>()))
                .Returns(Rejected(UnsubscribeTokenFailure.DecryptFailed));

            for (var i = 0; i < UnsubscribeWarningBudget.MaxWarningsPerWindow + 5; i++)
                await _client.GetAsync("/api/email/preferences?token=bad");

            var rejections = Warnings.Count(w => w.Message.Contains("DecryptFailed"));
            Assert.Equal(UnsubscribeWarningBudget.MaxWarningsPerWindow, rejections);

            var announcement = Assert.Single(Warnings, w => w.Message.Contains("suppressed"));
            Assert.Contains(nameof(UnsubscribeWarningKind.TokenRejection), announcement.Message);
        }

        [Fact]
        public async Task ExhaustingThePreTokenBudgetDoesNotSilenceTokenRejections()
        {
            // The confirmation check needs no credential at all, so a shared budget would let an
            // anonymous POST flood bury the evidence this whole feature exists to preserve.
            _tokenService
                .Setup(s => s.ValidateToken(It.IsAny<string>()))
                .Returns(Rejected(UnsubscribeTokenFailure.DecryptFailed));

            for (var i = 0; i < UnsubscribeWarningBudget.MaxWarningsPerWindow + 5; i++)
            {
                await _client.PostAsync(
                    "/api/email/unsubscribe/board",
                    new StringContent("Other=x", System.Text.Encoding.UTF8, "application/x-www-form-urlencoded")
                );
            }

            await _client.GetAsync("/api/email/preferences?token=bad");

            Assert.Contains(Warnings, w => w.Message.Contains("DecryptFailed"));
        }

        [Fact]
        public async Task SuppressingWarningsNeverSuppressesTheRejectionItself()
        {
            // The cap governs logging only. If the budget check were ever hoisted ahead of the
            // endpoint, request 101 would start returning 200 to a mailbox provider - unsubscribing
            // nobody while reporting success - and nothing else in the suite would notice.
            _tokenService
                .Setup(s => s.ValidateToken(It.IsAny<string>()))
                .Returns(Rejected(UnsubscribeTokenFailure.DecryptFailed));

            for (var i = 0; i < UnsubscribeWarningBudget.MaxWarningsPerWindow; i++)
                await _client.GetAsync("/api/email/preferences?token=bad");

            var loggedBefore = Warnings.Count(w => w.Message.Contains("DecryptFailed"));

            // Budget is now spent; the next request must still be rejected on its merits.
            var response = await _client.GetAsync("/api/email/preferences?token=bad");

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(UnsubscribeWarningBudget.MaxWarningsPerWindow, loggedBefore);
            Assert.Equal(loggedBefore, Warnings.Count(w => w.Message.Contains("DecryptFailed")));
        }

        // --- Log injection ---
        //
        // These belong here, not in the controller tests: the controller records rejections and
        // never logs them, so a VerifyNeverLogged against its own ILogger cannot fail whatever the
        // code does. Only the rendered middleware output can establish the guard.

        [Fact]
        public async Task AttackerControlledCategory_NeverReachesARenderedLogLine()
        {
            _tokenService.Setup(s => s.ValidateToken(It.IsAny<string>())).Returns(Valid(Guid.NewGuid(), "j@x.com"));

            var response = await _client.PostAsync(
                "/api/email/unsubscribe/board%0AInjectedLogLine?token=tok",
                new StringContent(
                    "List-Unsubscribe=One-Click",
                    System.Text.Encoding.UTF8,
                    "application/x-www-form-urlencoded"
                )
            );

            // Anchor on the expected rejection first: without it this test would pass simply by the
            // request never reaching the controller, which is the vacuous-green failure this file
            // exists to avoid.
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains(Warnings, w => w.Message.Contains("unknown-category"));
            Assert.DoesNotContain(Ours, e => e.Message.Contains("InjectedLogLine"));
        }

        [Fact]
        public async Task AttackerControlledConfirmationBody_NeverReachesARenderedLogLine()
        {
            var response = await _client.PostAsync(
                "/api/email/unsubscribe/board",
                new StringContent(
                    "List-Unsubscribe=" + Uri.EscapeDataString("One-Click\nInjectedLogLine"),
                    System.Text.Encoding.UTF8,
                    "application/x-www-form-urlencoded"
                )
            );

            // Anchor on the rejection first, as the sibling above does. A bare DoesNotContain
            // passes trivially when nothing was logged at all, and since the harness gained an
            // exception handler a throwing action no longer fails the test by escaping - it becomes
            // a 500 the middleware skips, which is exactly that vacuous green.
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains(Warnings, w => w.Message.Contains("confirmation-present-but-not-one-click"));
            Assert.DoesNotContain(Ours, e => e.Message.Contains("InjectedLogLine"));
        }

        // --- Route table vs the controller ---

        [Fact]
        public void EveryControllerRouteIsCoveredByTheMiddlewareRouteTable()
        {
            // The middleware repeats the controller's route prefixes as literals, because routing
            // rejects some requests before an action is selected and there is no descriptor to ask.
            // Nothing else ties the two together: if a route moves or a third action is added, the
            // routing-level 415/405 for it would stop being logged, silently. This locks them.
            var controller = typeof(UnsubscribeController);
            var prefix = controller
                .GetCustomAttributes(typeof(RouteAttribute), false)
                .Cast<RouteAttribute>()
                .Single()
                .Template;

            var templates = controller
                .GetMethods()
                .SelectMany(m => m.GetCustomAttributes(typeof(HttpMethodAttribute), false).Cast<HttpMethodAttribute>())
                .Select(a => a.Template)
                .Distinct();

            foreach (var template in templates)
            {
                // A null template binds the action to the controller-level route itself - the
                // ordinary way to add one, and precisely the shape that would escape the middleware
                // route table unnoticed. Filtering those out is what made an earlier version of this
                // test lock nothing.
                var path = new PathString(
                    template == null ? "/" + prefix : "/" + prefix + "/" + template.Split('/')[0]
                );
                Assert.True(
                    UnsubscribeDiagnostics.DescribeEndpoint(ContextFor(path)) != UnsubscribeDiagnostics.UnknownEndpoint,
                    $"No UnsubscribeRoutes entry covers {path} - rejections routing produces for it would go unlogged."
                );
            }
        }

        private static HttpContext ContextFor(PathString path)
        {
            var context = new DefaultHttpContext();
            context.Request.Path = path;
            return context;
        }

        [Theory]
        [InlineData("/api/email/preferences?token=bad", "LegacyToken")]
        // A link stripped in transit: the parameter survives, the credential does not. This is the
        // row that matters - it draws on the token budget (the evidence is worth protecting) while
        // contributing nothing to the legacy-redemption count.
        [InlineData("/api/email/preferences?token=", "None")]
        [InlineData("/api/email/preferences", "None")]
        public async Task CredentialTypeReflectsWhetherOneWasActuallyPresented(string url, string expected)
        {
            // The design doc makes CredentialType the counter that decides when the legacy path can
            // be retired ("removed when this reaches zero and holds"), so anything that is not a
            // real redemption must not be counted as one.
            _tokenService
                .Setup(s => s.ValidateToken(It.IsAny<string>()))
                .Returns(Rejected(UnsubscribeTokenFailure.Missing));

            var response = await _client.GetAsync(url);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains(Warnings, w => w.Message.Contains($"type {expected}"));
        }

        [Fact]
        public async Task AShortLinkIsCountedAsItsOwnCredentialType()
        {
            // The retirement counter is a single query grouping by type, so the new shape has to be
            // distinguishable from the legacy one rather than sharing its label.
            var response = await _client.GetAsync("/api/email/preferences?u=no-such-link");

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains(Warnings, w => w.Message.Contains("type ShortLink"));
            Assert.Contains(Warnings, w => w.Message.Contains("LinkNotFound"));
        }

        [Fact]
        public async Task AnEmptyShortLinkIdIsStillLoggedAndBilledToTheCredentialStream()
        {
            // `?id=` binds to null exactly as `?token=` does, and under short links this is now the
            // likeliest shape of a link stripped in transit - so it must be neither silent nor
            // billed to the budget an anonymous flood can drain.
            _tokenService
                .Setup(s => s.ValidateToken(It.IsAny<string>()))
                .Returns(Rejected(UnsubscribeTokenFailure.Missing));

            var response = await _client.GetAsync("/api/email/preferences?u=");

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains(Warnings, w => w.Message.Contains(nameof(UnsubscribeWarningKind.TokenRejection)));
        }

        [Fact]
        public async Task AShortLinkResolvesThroughTheRealPipeline()
        {
            // The end-to-end shape of the new credential: a stored link reaches the action and is
            // accepted, with no legacy token involved anywhere.
            var homeId = Guid.NewGuid();
            var link = await new UnsubscribeLinkIssuer(
                _linkRepository,
                TimeProvider.System,
                NullLogger<UnsubscribeLinkIssuer>.Instance
            ).IssueAsync(homeId, "jane@example.com");

            var home = new Home
            {
                Id = homeId,
                StreetNumber = 123,
                StreetName = "Oak Avenue",
                EmailAddress = new EmailAddress { Address = "jane@example.com", BoardEmailOptedIn = true },
            };
            _homeRepository.Setup(r => r.GetByIdAsync(homeId)).ReturnsAsync(home);
            _residentRepository.Setup(r => r.GetByHomeIdAsync(homeId)).ReturnsAsync(new List<Resident>());

            var response = await _client.GetAsync($"/api/email/preferences?u={link.Id}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.DoesNotContain(Warnings, w => w.Message.Contains("rejected"));
            _tokenService.Verify(s => s.ValidateToken(It.IsAny<string>()), Times.Never);
        }

        [Theory]
        [InlineData("?token=abc", "LegacyToken")]
        [InlineData("?token=", "None")]
        public async Task CredentialTypeIsAlsoRecordedOnPreActionRejections(string query, string expected)
        {
            // Exercises the OTHER log template - the one taken when routing rejects before any
            // action records a rejection. Both templates must carry the dimension or a query for an
            // incident sees only half of it.
            var response = await _client.PostAsync(
                "/api/email/unsubscribe/board" + query,
                new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
            );

            Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
            Assert.Contains(
                Warnings,
                w => w.Message.Contains("unsupported-media-type") && w.Message.Contains($"type {expected}")
            );
        }

        [Fact]
        public async Task BudgetNoticeSurvivesAFaultInTheRejectionLog()
        {
            // The notice is what makes the ensuing silence attributable, so it is emitted from a
            // finally. Without that, a fault in the rejection log - a mismatched template, a sink
            // outage - takes the notice with it and the suppression becomes indistinguishable from
            // a quiet endpoint. Nothing else in the suite would notice that.
            _logs.FailOnMessageContaining = "DecryptFailed";
            _tokenService
                .Setup(s => s.ValidateToken(It.IsAny<string>()))
                .Returns(Rejected(UnsubscribeTokenFailure.DecryptFailed));

            for (var i = 0; i < UnsubscribeWarningBudget.MaxWarningsPerWindow; i++)
                await _client.GetAsync("/api/email/preferences?token=bad");

            Assert.Contains(Ours, e => e.Message.Contains("suppressed"));
        }

        private sealed record RecordedLog(LogLevel Level, string Category, string Message);

        private sealed class RecordingLoggerProvider : ILoggerProvider
        {
            private readonly List<RecordedLog> _entries = new();

            /// <summary>When set, any log whose rendered message contains this throws instead.</summary>
            public string? FailOnMessageContaining { get; set; }

            public IReadOnlyList<RecordedLog> Entries
            {
                get
                {
                    lock (_entries)
                        return _entries.ToList();
                }
            }

            public ILogger CreateLogger(string categoryName) => new RecordingLogger(this, categoryName);

            public void Dispose() { }

            private void Add(RecordedLog entry)
            {
                lock (_entries)
                    _entries.Add(entry);
            }

            private sealed class RecordingLogger(RecordingLoggerProvider provider, string category) : ILogger
            {
                public IDisposable? BeginScope<TState>(TState state)
                    where TState : notnull => null;

                public bool IsEnabled(LogLevel logLevel) => true;

                public void Log<TState>(
                    LogLevel logLevel,
                    EventId eventId,
                    TState state,
                    Exception? exception,
                    Func<TState, Exception?, string> formatter
                )
                {
                    var message = formatter(state, exception);
                    if (
                        provider.FailOnMessageContaining is { } marker
                        && message.Contains(marker, StringComparison.Ordinal)
                    )
                    {
                        throw new InvalidOperationException("Injected logging fault.");
                    }

                    provider.Add(new RecordedLog(logLevel, category, message));
                }
            }
        }
    }
}
