using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Moq;
using Web.Controllers;
using Web.Models;
using Web.PresentationModels;
using Web.Services;
using Web.Services.Repositories;

namespace Web.UnitTests
{
    public class UnsubscribeControllerTests
    {
        private readonly Mock<IUnsubscribeTokenService> _tokenService = new();
        private readonly Mock<IHomeRepository> _homeRepository = new();
        private readonly Mock<IResidentRepository> _residentRepository = new();
        private readonly Mock<ILogger<UnsubscribeController>> _logger = new();

        private readonly DefaultHttpContext _httpContext = new();

        private UnsubscribeController CreateController()
        {
            return new UnsubscribeController(
                _tokenService.Object,
                _homeRepository.Object,
                _residentRepository.Object,
                _logger.Object
            )
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = _httpContext,
                    RouteData = new RouteData(),
                    ActionDescriptor = new ControllerActionDescriptor { ActionName = "TestAction" },
                },
            };
        }

        /// <summary>
        /// The rejection the action recorded for <see cref="UnsubscribeDiagnosticsMiddleware"/> to log.
        /// The action deliberately does not log rejections itself - see the middleware remarks - so
        /// these tests assert on what was recorded. Whether it reaches the log, and what the pipeline
        /// does with failures the action never sees, is covered by
        /// <see cref="UnsubscribeDiagnosticsPipelineTests"/> against a real MVC pipeline.
        /// </summary>
        private UnsubscribeRejection? Recorded() => UnsubscribeDiagnostics.Get(_httpContext);

        private static UnsubscribeTokenResult Valid(Guid homeId, string email) =>
            UnsubscribeTokenResult.Success(new UnsubscribeTokenPayload { HomeId = homeId, Email = email });

        private static UnsubscribeTokenResult Rejected(
            UnsubscribeTokenFailure failure = UnsubscribeTokenFailure.DecryptFailed
        ) => UnsubscribeTokenResult.Failed(failure);

        private static Resident CreateTestResident(Guid homeId, string email, bool allOptedIn = true)
        {
            return new Resident
            {
                Id = Guid.NewGuid(),
                HomeId = homeId,
                GivenName = "Jane",
                Surname = "Doe",
                EmailAddresses = new List<EmailAddress>
                {
                    new EmailAddress
                    {
                        Address = email,
                        BoardEmailOptedIn = allOptedIn,
                        WelcomeEmailOptedIn = allOptedIn,
                        GardenClubEmailOptedIn = allOptedIn,
                        SocialCommitteeEmailOptedIn = allOptedIn,
                        SunshineCommitteeEmailOptedIn = allOptedIn,
                    },
                },
            };
        }

        private static Home CreateTestHome(Guid id, string email, bool allOptedIn = true)
        {
            return new Home
            {
                Id = id,
                StreetNumber = 123,
                StreetName = "Oak Avenue",
                EmailAddress = new EmailAddress
                {
                    Address = "home@example.com",
                    BoardEmailOptedIn = true,
                    WelcomeEmailOptedIn = true,
                    GardenClubEmailOptedIn = true,
                    SocialCommitteeEmailOptedIn = true,
                    SunshineCommitteeEmailOptedIn = true,
                },
                Residents = new List<Resident>(),
            };
        }

        private void SetupResidentForHome(Guid homeId, Resident resident)
        {
            _residentRepository.Setup(r => r.GetByHomeIdAsync(homeId)).ReturnsAsync(new List<Resident> { resident });
            _residentRepository.Setup(r => r.UpsertAsync(It.IsAny<Resident>())).ReturnsAsync((Resident r) => r);
        }

        // --- OneClickUnsubscribe ---

        [Fact]
        public async Task OneClickUnsubscribe_InvalidToken_Returns400()
        {
            _tokenService.Setup(s => s.ValidateToken("bad")).Returns(Rejected());

            var controller = CreateController();
            var result = await controller.OneClickUnsubscribe("board", "bad", "One-Click");

            Assert.IsType<BadRequestObjectResult>(result);
        }

        [Fact]
        public async Task OneClickUnsubscribe_MissingFormBody_Returns400()
        {
            var controller = CreateController();
            var result = await controller.OneClickUnsubscribe("board", "tok", null!);

            Assert.IsType<BadRequestObjectResult>(result);
        }

        [Fact]
        public async Task OneClickUnsubscribe_UnknownCategory_Returns400()
        {
            var homeId = Guid.NewGuid();
            _tokenService.Setup(s => s.ValidateToken("tok")).Returns(Valid(homeId, "j@x.com"));

            var controller = CreateController();
            var result = await controller.OneClickUnsubscribe("unknown", "tok", "One-Click");

            Assert.IsType<BadRequestObjectResult>(result);
        }

        [Fact]
        public async Task OneClickUnsubscribe_HomeNotFound_Returns404()
        {
            var homeId = Guid.NewGuid();
            _tokenService.Setup(s => s.ValidateToken("tok")).Returns(Valid(homeId, "j@x.com"));
            _homeRepository.Setup(r => r.GetByIdAsync(homeId)).ReturnsAsync((Home?)null);

            var controller = CreateController();
            var result = await controller.OneClickUnsubscribe("board", "tok", "One-Click");

            Assert.IsType<NotFoundObjectResult>(result);
        }

        [Theory]
        [InlineData("board")]
        [InlineData("welcome")]
        [InlineData("garden")]
        [InlineData("social")]
        [InlineData("sunshine")]
        public async Task OneClickUnsubscribe_FlipsCategoryToFalse(string category)
        {
            var homeId = Guid.NewGuid();
            var email = "jane@example.com";
            var home = CreateTestHome(homeId, email, allOptedIn: true);
            var resident = CreateTestResident(homeId, email, allOptedIn: true);

            _tokenService.Setup(s => s.ValidateToken("tok")).Returns(Valid(homeId, email));
            _homeRepository.Setup(r => r.GetByIdAsync(homeId)).ReturnsAsync(home);
            _homeRepository.Setup(r => r.UpsertAsync(home)).ReturnsAsync(home);
            SetupResidentForHome(homeId, resident);

            var controller = CreateController();
            var result = await controller.OneClickUnsubscribe(category, "tok", "One-Click");

            Assert.IsType<OkObjectResult>(result);
            _homeRepository.Verify(r => r.UpsertAsync(home), Times.Once);

            var addr = resident.EmailAddresses[0];
            switch (category)
            {
                case "board":
                    Assert.False(addr.BoardEmailOptedIn);
                    break;
                case "welcome":
                    Assert.False(addr.WelcomeEmailOptedIn);
                    break;
                case "garden":
                    Assert.False(addr.GardenClubEmailOptedIn);
                    break;
                case "social":
                    Assert.False(addr.SocialCommitteeEmailOptedIn);
                    break;
                case "sunshine":
                    Assert.False(addr.SunshineCommitteeEmailOptedIn);
                    break;
            }
        }

        [Fact]
        public async Task OneClickUnsubscribe_EmailNotOnHome_Returns404()
        {
            var homeId = Guid.NewGuid();
            var home = CreateTestHome(homeId, "other@example.com");
            var resident = CreateTestResident(homeId, "other@example.com");

            _tokenService.Setup(s => s.ValidateToken("tok")).Returns(Valid(homeId, "missing@example.com"));
            _homeRepository.Setup(r => r.GetByIdAsync(homeId)).ReturnsAsync(home);
            SetupResidentForHome(homeId, resident);

            var controller = CreateController();
            var result = await controller.OneClickUnsubscribe("board", "tok", "One-Click");

            Assert.IsType<NotFoundObjectResult>(result);
        }

        [Fact]
        public async Task OneClickUnsubscribe_CaseInsensitiveEmailMatch()
        {
            var homeId = Guid.NewGuid();
            var home = CreateTestHome(homeId, "Jane@Example.COM");
            var resident = CreateTestResident(homeId, "Jane@Example.COM");

            _tokenService.Setup(s => s.ValidateToken("tok")).Returns(Valid(homeId, "jane@example.com"));
            _homeRepository.Setup(r => r.GetByIdAsync(homeId)).ReturnsAsync(home);
            _homeRepository.Setup(r => r.UpsertAsync(home)).ReturnsAsync(home);
            SetupResidentForHome(homeId, resident);

            var controller = CreateController();
            var result = await controller.OneClickUnsubscribe("board", "tok", "One-Click");

            Assert.IsType<OkObjectResult>(result);
            Assert.False(resident.EmailAddresses[0].BoardEmailOptedIn);
        }

        // --- GetPreferences ---

        [Fact]
        public async Task GetPreferences_InvalidToken_Returns400()
        {
            _tokenService.Setup(s => s.ValidateToken("bad")).Returns(Rejected());

            var controller = CreateController();
            var result = await controller.GetPreferences("bad");

            Assert.IsType<BadRequestObjectResult>(result);
        }

        [Fact]
        public async Task GetPreferences_ReturnsAggregatedPreferences()
        {
            var homeId = Guid.NewGuid();
            var email = "jane@example.com";
            var home = CreateTestHome(homeId, email, allOptedIn: true);
            var resident = CreateTestResident(homeId, email, allOptedIn: true);

            _tokenService.Setup(s => s.ValidateToken("tok")).Returns(Valid(homeId, email));
            _homeRepository.Setup(r => r.GetByIdAsync(homeId)).ReturnsAsync(home);
            SetupResidentForHome(homeId, resident);

            var controller = CreateController();
            var result = await controller.GetPreferences("tok") as OkObjectResult;

            Assert.NotNull(result);
            var dto = Assert.IsType<EmailPreferencesDto>(result.Value);
            Assert.Equal(email, dto.Email);
            Assert.Equal("123 Oak Avenue", dto.HomeName);
            Assert.True(dto.BoardEmailOptedIn);
            Assert.True(dto.WelcomeEmailOptedIn);
            Assert.True(dto.GardenClubEmailOptedIn);
            Assert.True(dto.SocialCommitteeEmailOptedIn);
            Assert.True(dto.SunshineCommitteeEmailOptedIn);
        }

        // --- UpdatePreferences ---

        [Fact]
        public async Task UpdatePreferences_InvalidToken_Returns400()
        {
            _tokenService.Setup(s => s.ValidateToken("bad")).Returns(Rejected());

            var controller = CreateController();
            var result = await controller.UpdatePreferences("bad", new UpdateEmailPreferencesDto());

            Assert.IsType<BadRequestObjectResult>(result);
        }

        [Fact]
        public async Task UpdatePreferences_NullBody_Returns400()
        {
            _tokenService.Setup(s => s.ValidateToken("tok")).Returns(Valid(Guid.NewGuid(), "x@x.com"));

            var controller = CreateController();
            var result = await controller.UpdatePreferences("tok", (UpdateEmailPreferencesDto)null!);

            Assert.IsType<BadRequestObjectResult>(result);
        }

        [Fact]
        public async Task UpdatePreferences_SavesNewValues()
        {
            var homeId = Guid.NewGuid();
            var email = "jane@example.com";
            var home = CreateTestHome(homeId, email, allOptedIn: true);
            var resident = CreateTestResident(homeId, email, allOptedIn: true);

            _tokenService.Setup(s => s.ValidateToken("tok")).Returns(Valid(homeId, email));
            _homeRepository.Setup(r => r.GetByIdAsync(homeId)).ReturnsAsync(home);
            _homeRepository.Setup(r => r.UpsertAsync(home)).ReturnsAsync(home);
            SetupResidentForHome(homeId, resident);

            var controller = CreateController();
            var dto = new UpdateEmailPreferencesDto
            {
                BoardEmailOptedIn = false,
                WelcomeEmailOptedIn = true,
                GardenClubEmailOptedIn = false,
                SocialCommitteeEmailOptedIn = true,
                SunshineCommitteeEmailOptedIn = false,
            };
            var result = await controller.UpdatePreferences("tok", dto);

            Assert.IsType<OkObjectResult>(result);
            _homeRepository.Verify(r => r.UpsertAsync(home), Times.Once);

            var addr = resident.EmailAddresses[0];
            Assert.False(addr.BoardEmailOptedIn);
            Assert.True(addr.WelcomeEmailOptedIn);
            Assert.False(addr.GardenClubEmailOptedIn);
            Assert.True(addr.SocialCommitteeEmailOptedIn);
            Assert.False(addr.SunshineCommitteeEmailOptedIn);
        }

        [Fact]
        public async Task UpdatePreferences_UpdatesBothResidentAndHomeEmailAddresses()
        {
            var homeId = Guid.NewGuid();
            var email = "shared@example.com";
            var home = new Home
            {
                Id = homeId,
                StreetNumber = 99,
                StreetName = "Test St",
                EmailAddress = new EmailAddress
                {
                    Address = email,
                    BoardEmailOptedIn = true,
                    WelcomeEmailOptedIn = true,
                    GardenClubEmailOptedIn = true,
                    SocialCommitteeEmailOptedIn = true,
                    SunshineCommitteeEmailOptedIn = true,
                },
                Residents = new List<Resident>(),
            };
            var resident = new Resident
            {
                Id = Guid.NewGuid(),
                HomeId = homeId,
                GivenName = "Test",
                Surname = "User",
                EmailAddresses = new List<EmailAddress>
                {
                    new EmailAddress
                    {
                        Address = email,
                        BoardEmailOptedIn = true,
                        WelcomeEmailOptedIn = true,
                        GardenClubEmailOptedIn = true,
                        SocialCommitteeEmailOptedIn = true,
                        SunshineCommitteeEmailOptedIn = true,
                    },
                },
            };

            _tokenService.Setup(s => s.ValidateToken("tok")).Returns(Valid(homeId, email));
            _homeRepository.Setup(r => r.GetByIdAsync(homeId)).ReturnsAsync(home);
            _homeRepository.Setup(r => r.UpsertAsync(home)).ReturnsAsync(home);
            SetupResidentForHome(homeId, resident);

            var controller = CreateController();
            var dto = new UpdateEmailPreferencesDto { BoardEmailOptedIn = false };
            await controller.UpdatePreferences("tok", dto);

            // Both the resident and home-level email should be updated
            Assert.False(resident.EmailAddresses[0].BoardEmailOptedIn);
            Assert.False(home.EmailAddress.BoardEmailOptedIn);
        }

        // --- Concurrency retry ---

        [Fact]
        public async Task OneClickUnsubscribe_RetriesOnConcurrencyConflict()
        {
            var homeId = Guid.NewGuid();
            var email = "jane@example.com";

            _tokenService.Setup(s => s.ValidateToken("tok")).Returns(Valid(homeId, email));

            // First call returns a home, but upsert throws concurrency conflict
            var home1 = CreateTestHome(homeId, email, allOptedIn: true);
            var home2 = CreateTestHome(homeId, email, allOptedIn: true);
            var resident1 = CreateTestResident(homeId, email, allOptedIn: true);
            var resident2 = CreateTestResident(homeId, email, allOptedIn: true);

            var getCallCount = 0;
            _homeRepository.Setup(r => r.GetByIdAsync(homeId)).ReturnsAsync(() => getCallCount++ == 0 ? home1 : home2);

            var residentCallCount = 0;
            _residentRepository
                .Setup(r => r.GetByHomeIdAsync(homeId))
                .ReturnsAsync(() => new List<Resident> { residentCallCount++ == 0 ? resident1 : resident2 });
            _residentRepository.Setup(r => r.UpsertAsync(It.IsAny<Resident>())).ReturnsAsync((Resident r) => r);

            var upsertCallCount = 0;
            _homeRepository
                .Setup(r => r.UpsertAsync(It.IsAny<Home>()))
                .Returns<Home>(h =>
                {
                    if (upsertCallCount++ == 0)
                        throw new ConcurrencyConflictException("conflict", new Exception());
                    return Task.FromResult(h);
                });

            var controller = CreateController();
            var result = await controller.OneClickUnsubscribe("board", "tok", "One-Click");

            Assert.IsType<OkObjectResult>(result);
            // Should have been called twice (first attempt + retry)
            Assert.Equal(2, getCallCount);
            Assert.Equal(2, upsertCallCount);
            // The second resident object should have the preference flipped
            Assert.False(resident2.EmailAddresses[0].BoardEmailOptedIn);
        }

        [Fact]
        public async Task OneClickUnsubscribe_ReturnsConflictAfterMaxRetries()
        {
            var homeId = Guid.NewGuid();
            var email = "jane@example.com";

            _tokenService.Setup(s => s.ValidateToken("tok")).Returns(Valid(homeId, email));

            _homeRepository
                .Setup(r => r.GetByIdAsync(homeId))
                .ReturnsAsync(() => CreateTestHome(homeId, email, allOptedIn: true));

            _residentRepository
                .Setup(r => r.GetByHomeIdAsync(homeId))
                .ReturnsAsync(() => new List<Resident> { CreateTestResident(homeId, email, allOptedIn: true) });
            _residentRepository.Setup(r => r.UpsertAsync(It.IsAny<Resident>())).ReturnsAsync((Resident r) => r);

            _homeRepository
                .Setup(r => r.UpsertAsync(It.IsAny<Home>()))
                .ThrowsAsync(new ConcurrencyConflictException("conflict", new Exception()));

            var controller = CreateController();
            var result = await controller.OneClickUnsubscribe("board", "tok", "One-Click");

            Assert.IsType<ConflictObjectResult>(result);
        }

        // --- Diagnostics ---
        //
        // These lock what the action *records* for the diagnostics middleware. Whether a record
        // reaches the log, at what level, and what happens to the failures the action never sees is
        // covered by UnsubscribeDiagnosticsPipelineTests against a real MVC pipeline - deliberately
        // not here, because a test that invokes the action directly cannot observe any of it.

        [Fact]
        public async Task RejectedCredential_WithoutATokenDrawsOnThePreTokenBudget()
        {
            // ValidateToken(null) returns Missing, so a bare tokenless GET reaches the rejection
            // path. Billing it to the token stream would let crawler noise drain the budget that
            // protects real mangled-link evidence.
            _tokenService
                .Setup(s => s.ValidateToken(It.IsAny<string>()))
                .Returns(Rejected(UnsubscribeTokenFailure.Missing));

            var controller = CreateController();
            await controller.GetPreferences(null!);

            Assert.Equal(UnsubscribeWarningKind.PreTokenRejection, Recorded()!.Kind);
        }

        [Fact]
        public async Task RejectedCredential_NeverRecordsTheTokenItself()
        {
            const string secret = "abcdefghijklmnopqrstuvwxyz0123456789";
            _tokenService.Setup(s => s.ValidateToken(secret)).Returns(Rejected());

            var controller = CreateController();
            await controller.GetPreferences(secret);

            // Assert the exact disclosure, not merely that the whole 36-character secret is absent
            // from an 11-character field - that comparison cannot fail and would stay green if the
            // helper were changed to disclose sixteen characters at each end.
            Assert.Equal("abcd...6789", Recorded()!.TokenEnds);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task AbsentOrEmptiedCredential_StillRecordsARejection(string? token)
        {
            // ASP.NET Core binds an empty `?token=` to null, exactly like an absent parameter, so
            // the controller cannot tell a stripped link from a bare crawler hit. An earlier
            // revision demoted this reason to Debug for flood control and thereby silenced the
            // stripped-link signal this work exists to capture. Volume is bounded by the budget
            // instead, which discards no class of evidence.
            _tokenService
                .Setup(s => s.ValidateToken(It.IsAny<string>()))
                .Returns(Rejected(UnsubscribeTokenFailure.Missing));

            var controller = CreateController();
            await controller.GetPreferences(token!);

            // The point of this test is that the rejection is recorded at all. Which budget it
            // draws on is decided by token presence and is locked separately.
            Assert.Equal(UnsubscribeTokenFailure.Missing, Recorded()?.Failure);
        }

        [Fact]
        public async Task RejectedCredential_RecordsLengthAndRedactedEndsButNeverTheToken()
        {
            var secret = "abcd" + new string('m', 127) + "wxyz";
            _tokenService.Setup(s => s.ValidateToken(secret)).Returns(Rejected(UnsubscribeTokenFailure.DecryptFailed));

            var controller = CreateController();
            await controller.GetPreferences(secret);

            var recorded = Recorded();
            Assert.Equal(135, recorded!.TokenLength);
            Assert.Equal("abcd...wxyz", recorded.TokenEnds);
            Assert.DoesNotContain(secret, recorded.TokenEnds);
        }

        [Fact]
        public async Task AcceptedCredential_LogsCredentialTypeAndRecordsNoRejection()
        {
            var homeId = Guid.NewGuid();
            var email = "jane@example.com";
            _tokenService.Setup(s => s.ValidateToken("tok")).Returns(Valid(homeId, email));
            _homeRepository.Setup(r => r.GetByIdAsync(homeId)).ReturnsAsync(CreateTestHome(homeId, email));
            SetupResidentForHome(homeId, CreateTestResident(homeId, email));

            var controller = CreateController();
            await controller.GetPreferences("tok");

            VerifyLogged(LogLevel.Information, m => m.Contains("LegacyToken"));
            Assert.Null(Recorded());
        }

        [Fact]
        public async Task OneClickUnsubscribe_ConfirmationRejection_WithoutATokenDrawsOnThePreTokenBudget()
        {
            // A tokenless POST is the cheapest thing to flood, so it must not draw on the stream
            // that carries the stripped-link evidence. The query string is left empty deliberately:
            // that is what makes this the tokenless case.
            var controller = CreateController();
            var result = await controller.OneClickUnsubscribe("board", null!, null!);

            Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal(UnsubscribeWarningKind.PreTokenRejection, Recorded()!.Kind);
        }

        [Fact]
        public async Task OneClickUnsubscribe_ConfirmationRejection_WithATokenDrawsOnTheTokenBudget()
        {
            // A provider changing its RFC 8058 body is the regression worth catching, and those
            // POSTs carry a real token. Billing them to the budget an empty POST can flood would let
            // a crawler suppress exactly the signal the split exists to protect.
            _httpContext.Request.QueryString = new QueryString("?token=abc");

            var controller = CreateController();
            await controller.OneClickUnsubscribe("board", "abc", "not-one-click");

            Assert.Equal(UnsubscribeWarningKind.TokenRejection, Recorded()!.Kind);
        }

        [Fact]
        public async Task OneClickUnsubscribe_ConfirmationRejection_NeverRecordsTheSuppliedValue()
        {
            const string hostile = "One-Click\nInjected log line";

            var controller = CreateController();
            await controller.OneClickUnsubscribe("board", "tok", hostile);

            Assert.DoesNotContain("Injected", Recorded()!.Reason);
        }

        // --- Rejections that happen after the credential was accepted ---
        //
        // These returned 4xx silently. The token is valid, but the SPA renders every failure with
        // the same "the link may be invalid or expired" text, so the resident dead-ends while the
        // log shows only an acceptance - an operator reading it would conclude the request worked.

        [Fact]
        public async Task GetPreferences_HomeNotFound_RecordsARejection()
        {
            var homeId = Guid.NewGuid();
            _tokenService.Setup(s => s.ValidateToken("tok")).Returns(Valid(homeId, "jane@example.com"));
            _homeRepository.Setup(r => r.GetByIdAsync(homeId)).ReturnsAsync((Home?)null);

            var controller = CreateController();
            var result = await controller.GetPreferences("tok");

            Assert.IsType<NotFoundObjectResult>(result);
            Assert.Equal("home-not-found", Recorded()?.Reason);
        }

        [Fact]
        public async Task GetPreferences_EmailNotOnHome_RecordsARejection()
        {
            var homeId = Guid.NewGuid();
            _tokenService.Setup(s => s.ValidateToken("tok")).Returns(Valid(homeId, "missing@example.com"));
            _homeRepository
                .Setup(r => r.GetByIdAsync(homeId))
                .ReturnsAsync(CreateTestHome(homeId, "other@example.com"));
            SetupResidentForHome(homeId, CreateTestResident(homeId, "other@example.com"));

            var controller = CreateController();
            var result = await controller.GetPreferences("tok");

            Assert.IsType<NotFoundObjectResult>(result);
            Assert.Equal("email-not-on-home", Recorded()?.Reason);
        }

        [Fact]
        public async Task OneClickUnsubscribe_UnknownCategory_RecordsWithoutEchoingTheCategory()
        {
            var homeId = Guid.NewGuid();
            _tokenService.Setup(s => s.ValidateToken("tok")).Returns(Valid(homeId, "j@x.com"));

            var controller = CreateController();
            await controller.OneClickUnsubscribe("unknown\ninjected", "tok", "One-Click");

            Assert.Equal("unknown-category", Recorded()?.Reason);
        }

        [Fact]
        public async Task UpdatePreferences_NullBody_RecordsARejection()
        {
            // Unreachable over HTTP - [ApiController] rejects an absent body first, which the
            // pipeline tests cover - but the guard must still not fall through to an NRE.
            _tokenService.Setup(s => s.ValidateToken("tok")).Returns(Valid(Guid.NewGuid(), "x@x.com"));

            var controller = CreateController();
            await controller.UpdatePreferences("tok", null!);

            Assert.Equal("missing-request-body", Recorded()?.Reason);
        }

        [Fact]
        public void DescribeTokenEnds_NeutralisesCharactersThatCouldForgeALogLine()
        {
            // A percent-encoded newline in ?token= would otherwise split the rendered message and
            // let an anonymous caller author what looks like a second, genuine entry.
            var token = "a\r\nb" + new string('x', MinLengthForEndDisclosureMinusEight) + "c\td!";
            var described = UnsubscribeController.DescribeTokenEnds(token);

            Assert.Equal("a..b...c.d.", described);
            Assert.DoesNotContain('\n', described);
            Assert.DoesNotContain('\r', described);
            Assert.DoesNotContain('\t', described);
        }

        private const int MinLengthForEndDisclosureMinusEight = UnsubscribeController.MinLengthForEndDisclosure - 8;

        [Theory]
        [InlineData(null, "absent")]
        [InlineData("", "blank")]
        [InlineData("   ", "blank")]
        [InlineData("abcd", "withheld")]
        [InlineData("abcdefgh", "withheld")]
        public void DescribeTokenEnds_ShortOrAbsentTokensDoNotExposeCharacters(string? token, string expected)
        {
            Assert.Equal(expected, UnsubscribeController.DescribeTokenEnds(token!));
        }

        [Fact]
        public void DescribeTokenEnds_WithholdsEndsForCredentialsShorterThanTheThreshold()
        {
            // head+tail is eight characters, so disclosing the ends of a short credential - the
            // typed recovery code in Part 2 of the design doc is nine - hands over nearly all of it.
            var justUnder = new string('x', UnsubscribeController.MinLengthForEndDisclosure - 1);
            Assert.Equal("withheld", UnsubscribeController.DescribeTokenEnds(justUnder));
        }

        [Fact]
        public void DescribeTokenEnds_ReportsEndsOnlyForTokensLongEnoughToStayOpaque()
        {
            var token = "abcd" + new string('m', UnsubscribeController.MinLengthForEndDisclosure - 8) + "wxyz";
            Assert.Equal(UnsubscribeController.MinLengthForEndDisclosure, token.Length);
            Assert.Equal("abcd...wxyz", UnsubscribeController.DescribeTokenEnds(token));
        }

        [Fact]
        public void DescribeTokenEnds_NeverDisclosesMoreThanEightCharacters()
        {
            // Locks the disclosure budget itself, so widening head/tail fails here rather than in
            // production. A real legacy token is ~135 characters.
            var token = string.Concat(Enumerable.Range(0, 135).Select(i => (char)('a' + (i % 26))));
            var described = UnsubscribeController.DescribeTokenEnds(token);

            var disclosed = described.Replace("...", string.Empty);
            Assert.Equal(8, disclosed.Length);
            Assert.All(disclosed, c => Assert.Contains(c, token));
        }

        private void VerifyLogged(LogLevel level, Func<string, bool> messageMatches)
        {
            _logger.Verify(
                l =>
                    l.Log(
                        level,
                        It.IsAny<EventId>(),
                        It.Is<It.IsAnyType>((v, _) => messageMatches(v.ToString()!)),
                        It.IsAny<Exception>(),
                        It.IsAny<Func<It.IsAnyType, Exception?, string>>()
                    ),
                Times.Once
            );
        }
    }
}
