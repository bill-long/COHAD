using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Web.Controllers;
using Web.MockData;
using Web.Models;
using Web.PresentationModels;
using Web.Services;
using Web.Services.Repositories;
using Xunit;

namespace Web.UnitTests
{
    public class EmailSuppressionControllerTests
    {
        private const string IdentityProviderClaim = "http://schemas.microsoft.com/identity/claims/identityprovider";
        private const string AdminNameId = "admin1";
        private const string AdminIdp = "google.com";
        private static string AdminUniqueId => $"{AdminIdp}{AdminNameId}";

        private readonly MockEmailSuppressionRepository _suppressions = new();
        private readonly Mock<IAuditLogRepository> _auditLog = new();
        private readonly Mock<IUserRepository> _users = new();
        private readonly Mock<IPostmarkReactivationService> _reactivation = new();
        private readonly TestClock _time = new();

        public EmailSuppressionControllerTests()
        {
            _users
                .Setup(r => r.GetByUniqueIdAsync(AdminUniqueId))
                .ReturnsAsync(
                    new User
                    {
                        UniqueId = AdminUniqueId,
                        GivenName = "Ada",
                        Surname = "Admin",
                        Roles = new List<User.Role> { User.Role.Administrator },
                    }
                );
            // Provider-side reactivation succeeds unless a test says otherwise.
            _reactivation
                .Setup(r => r.ReactivateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PostmarkReactivationResult(2, Array.Empty<string>()));
        }

        private EmailSuppressionController CreateController()
        {
            return new EmailSuppressionController(
                _suppressions,
                new EmailSuppressionService(_suppressions, _time),
                _auditLog.Object,
                new CurrentUserAccessor(_users.Object),
                _reactivation.Object
            )
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(
                            new ClaimsIdentity(
                                new[]
                                {
                                    new Claim(ClaimTypes.NameIdentifier, AdminNameId),
                                    new Claim(IdentityProviderClaim, AdminIdp),
                                },
                                "Test"
                            )
                        ),
                    },
                },
            };
        }

        private async Task<EmailSuppression> SeedAsync(string email, SuppressionReason reason, DateTimeOffset when)
        {
            var clock = new TestClock(when);
            var outcome = await new EmailSuppressionService(_suppressions, clock).RecordAsync(
                email,
                reason,
                reason == SuppressionReason.AdminAction ? "some-admin" : EmailSuppression.SystemDeliveryEvent,
                null,
                null
            );
            return outcome.Suppression;
        }

        [Fact]
        public void EveryEndpointSitsBehindTheAdministratorPolicy()
        {
            // The controller-level attribute is the authorization boundary; direct action
            // invocation cannot exercise it, so its presence is locked instead - removing or
            // weakening it should fail a test, not a pen test.
            var attribute = typeof(EmailSuppressionController).GetCustomAttribute<AuthorizeAttribute>();

            Assert.NotNull(attribute);
            Assert.Equal("Administrator", attribute!.Policy);
        }

        // --- List ---

        [Fact]
        public async Task List_ReturnsActiveOnlyByDefault_NewestFirst()
        {
            await SeedAsync("older@example.com", SuppressionReason.HardBounce, new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
            await SeedAsync("newer@example.com", SuppressionReason.SpamComplaint, new DateTimeOffset(2026, 8, 5, 0, 0, 0, TimeSpan.Zero));
            var cleared = await SeedAsync("cleared@example.com", SuppressionReason.HardBounce, new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero));
            await new EmailSuppressionService(_suppressions, _time).ClearAsync(cleared.Email, "some-admin");

            var controller = CreateController();
            var result = await controller.List() as OkObjectResult;

            var list = Assert.IsType<List<EmailSuppressionDto>>(result!.Value);
            Assert.Equal(new[] { "newer@example.com", "older@example.com" }, list.Select(s => s.Email).ToArray());
            Assert.All(list, s => Assert.True(s.IsActive));
        }

        [Fact]
        public async Task List_IncludeClearedAddsTheHistoryRows()
        {
            var cleared = await SeedAsync("cleared@example.com", SuppressionReason.HardBounce, new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero));
            await new EmailSuppressionService(_suppressions, _time).ClearAsync(cleared.Email, "some-admin");

            var controller = CreateController();
            var result = await controller.List(includeCleared: true) as OkObjectResult;

            var list = Assert.IsType<List<EmailSuppressionDto>>(result!.Value);
            var row = Assert.Single(list);
            Assert.False(row.IsActive);
            Assert.Equal("some-admin", row.ClearedBy);
        }

        // --- Create ---

        [Fact]
        public async Task Create_WritesAnAdminActionSuppressionAndAudits()
        {
            NewAuditLogEntry entry = null;
            _auditLog
                .Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>()))
                .Callback<NewAuditLogEntry>(e => entry = e)
                .Returns(Task.CompletedTask);

            var controller = CreateController();
            var result = await controller.Create(new CreateEmailSuppressionDto { Email = "Jane@Example.com" }) as OkObjectResult;

            var dto = Assert.IsType<EmailSuppressionDto>(result!.Value);
            Assert.Equal("jane@example.com", dto.Email);
            Assert.Equal(SuppressionReason.AdminAction, dto.Reason);
            Assert.Equal(AdminUniqueId, dto.SuppressedBy);
            Assert.True(dto.IsActive);

            Assert.NotNull(entry);
            Assert.Equal(AdminUniqueId, entry.UserId);
            Assert.Equal("Ada Admin", entry.UserDisplayName);
            // Redacted in the audit log, full only on the Administrator page.
            Assert.Equal("jan***@example.com", entry.SubjectId);
            Assert.DoesNotContain("jane@example.com", entry.Action);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not-an-address")]
        public async Task Create_RejectsAnUnusableAddress(string email)
        {
            var controller = CreateController();
            var result = await controller.Create(new CreateEmailSuppressionDto { Email = email });

            Assert.IsType<BadRequestObjectResult>(result);
            Assert.Empty(await _suppressions.GetAllAsync());
        }

        [Fact]
        public async Task Create_ForAnAlreadySuppressedAddressCountsTheRepeat()
        {
            await SeedAsync("jane@example.com", SuppressionReason.HardBounce, new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));

            var controller = CreateController();
            var result = await controller.Create(new CreateEmailSuppressionDto { Email = "jane@example.com" }) as OkObjectResult;

            var dto = Assert.IsType<EmailSuppressionDto>(result!.Value);
            Assert.Equal(2, dto.ConsecutiveFailureCount);
            // Repeat evidence on an active record keeps the original why.
            Assert.Equal(SuppressionReason.HardBounce, dto.Reason);
            Assert.Single(await _suppressions.GetAllAsync());
        }

        // --- Clear ---

        [Fact]
        public async Task Clear_StampsWhoClearedAndAudits()
        {
            var seeded = await SeedAsync("jane@example.com", SuppressionReason.SpamComplaint, new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));

            NewAuditLogEntry entry = null;
            _auditLog
                .Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>()))
                .Callback<NewAuditLogEntry>(e => entry = e)
                .Returns(Task.CompletedTask);

            var controller = CreateController();
            var result = await controller.Clear(seeded.Id) as OkObjectResult;

            var dto = Assert.IsType<EmailSuppressionDto>(result!.Value);
            Assert.False(dto.IsActive);
            Assert.Equal(AdminUniqueId, dto.ClearedBy);

            Assert.NotNull(entry);
            Assert.Contains("SpamComplaint", entry.Action);
            Assert.Equal("jan***@example.com", entry.SubjectId);
            // A spam-complaint suppression has no provider-unsubscribe entry to lift.
            _reactivation.Verify(
                r => r.ReactivateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never
            );
        }

        [Fact]
        public async Task Clear_UnknownId_Returns404()
        {
            var controller = CreateController();
            var result = await controller.Clear("no-such-id");

            Assert.IsType<NotFoundObjectResult>(result);
        }

        [Fact]
        public async Task Clear_AlreadyCleared_IsIdempotentAndDoesNotReaudit()
        {
            // The original clear's stamps are the truthful ones, and an audit entry saying this
            // admin cleared it would attribute an action they did not take.
            var seeded = await SeedAsync("jane@example.com", SuppressionReason.HardBounce, new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
            await new EmailSuppressionService(_suppressions, _time).ClearAsync(seeded.Email, "first-admin");

            var controller = CreateController();
            var result = await controller.Clear(seeded.Id) as OkObjectResult;

            var dto = Assert.IsType<EmailSuppressionDto>(result!.Value);
            Assert.False(dto.IsActive);
            Assert.Equal("first-admin", dto.ClearedBy);
            _auditLog.Verify(r => r.AddAsync(It.IsAny<NewAuditLogEntry>()), Times.Never);
        }

        // --- Clear: provider-side reactivation (issue #11) ---

        [Fact]
        public async Task Clear_ProviderUnsubscribe_ReactivatesAtTheProviderThenClearsAndAudits()
        {
            var seeded = await SeedAsync("jane@example.com", SuppressionReason.ProviderUnsubscribe, new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));

            NewAuditLogEntry entry = null;
            _auditLog
                .Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>()))
                .Callback<NewAuditLogEntry>(e => entry = e)
                .Returns(Task.CompletedTask);

            var controller = CreateController();
            var result = await controller.Clear(seeded.Id) as OkObjectResult;

            var dto = Assert.IsType<EmailSuppressionDto>(result!.Value);
            Assert.False(dto.IsActive);
            _reactivation.Verify(
                r => r.ReactivateAsync("jane@example.com", It.IsAny<CancellationToken>()),
                Times.Once
            );
            Assert.NotNull(entry);
            Assert.Contains("Also reactivated the address at the email provider", entry.Action);
        }

        [Fact]
        public async Task Clear_ProviderUnsubscribe_FailedReactivationFailsTheRequestAndClearsNothing()
        {
            // Provider-first, by design: lifting only COHAD's record would resume "successful"
            // sends the provider silently drops, so a failed reactivation refuses the clear
            // (502) and the record stays in force - clicking Clear again is the whole retry.
            var seeded = await SeedAsync("jane@example.com", SuppressionReason.ProviderUnsubscribe, new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
            _reactivation
                .Setup(r => r.ReactivateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(
                    new PostmarkReactivationResult(
                        2,
                        new[] { "broadcast", "outbound" },
                        "SpamComplaint suppressions cannot be deleted."
                    )
                );

            var controller = CreateController();
            var result = await controller.Clear(seeded.Id);

            var status = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status502BadGateway, status.StatusCode);
            // The error names the address and carries the provider's own refusal text - what
            // distinguishes a retryable outage from a permanent refusal.
            Assert.Contains("jane@example.com", status.Value!.ToString());
            Assert.Contains("SpamComplaint suppressions cannot be deleted", status.Value!.ToString());
            var record = await _suppressions.GetByIdAsync(seeded.Id);
            Assert.True(record!.IsActive);
            // Every stream failed: nothing changed provider-side, so there is nothing to audit.
            _auditLog.Verify(r => r.AddAsync(It.IsAny<NewAuditLogEntry>()), Times.Never);
        }

        [Fact]
        public async Task Clear_PartialReactivationFailure_Returns502ButAuditsTheRealDeletions()
        {
            // One stream's entry WAS deleted before the other refused: that provider-side change
            // is real, so it is audited even though the clear itself is refused - the audit log
            // must be able to explain why one stream no longer suppresses an address COHAD
            // still does.
            var seeded = await SeedAsync("jane@example.com", SuppressionReason.ProviderUnsubscribe, new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
            _reactivation
                .Setup(r => r.ReactivateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PostmarkReactivationResult(2, new[] { "outbound" }, "boom"));

            NewAuditLogEntry entry = null;
            _auditLog
                .Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>()))
                .Callback<NewAuditLogEntry>(e => entry = e)
                .Returns(Task.CompletedTask);

            var controller = CreateController();
            var result = await controller.Clear(seeded.Id);

            var status = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status502BadGateway, status.StatusCode);
            Assert.True((await _suppressions.GetByIdAsync(seeded.Id))!.IsActive);
            Assert.NotNull(entry);
            Assert.Contains("1 of 2 streams", entry.Action);
            Assert.Contains("NOT cleared", entry.Action);
        }

        [Fact]
        public async Task Clear_WithADifferentDisplayedEpisode_Returns409WithoutTouchingTheProvider()
        {
            // The admin's page showed an episode this record no longer describes (it was
            // re-suppressed since): nothing is cleared and the provider is not called - the
            // admin must see the new episode before mail resumes.
            var seeded = await SeedAsync("jane@example.com", SuppressionReason.ProviderUnsubscribe, new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));

            var controller = CreateController();
            var result = await controller.Clear(
                seeded.Id,
                new ClearEmailSuppressionRequestDto { SuppressedUtc = seeded.SuppressedUtc.AddMinutes(-5) }
            );

            Assert.IsType<ConflictObjectResult>(result);
            Assert.True((await _suppressions.GetByIdAsync(seeded.Id))!.IsActive);
            _reactivation.Verify(
                r => r.ReactivateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never
            );
            _auditLog.Verify(r => r.AddAsync(It.IsAny<NewAuditLogEntry>()), Times.Never);
        }

        [Fact]
        public async Task Clear_MatchingDisplayedEpisode_Clears()
        {
            var seeded = await SeedAsync("jane@example.com", SuppressionReason.ProviderUnsubscribe, new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));

            var controller = CreateController();
            var result = await controller.Clear(
                seeded.Id,
                new ClearEmailSuppressionRequestDto { SuppressedUtc = seeded.SuppressedUtc }
            ) as OkObjectResult;

            var dto = Assert.IsType<EmailSuppressionDto>(result!.Value);
            Assert.False(dto.IsActive);
        }

        [Fact]
        public async Task Clear_BlankEmailProviderUnsubscribeRow_ClearsWithoutAProviderCall()
        {
            // The by-id clear exists so any listed row is clearable, including a hand-authored
            // or corrupt one. A blank address cannot be suppressed at the provider, so the
            // provider is not asked about it - asking would only manufacture an unresolvable
            // failure that leaves the row permanently stuck.
            var handAuthored = new EmailSuppression
            {
                Id = "hand-authored-row",
                Email = "   ",
                Reason = SuppressionReason.ProviderUnsubscribe,
                ConsecutiveFailureCount = 1,
                FirstSeenUtc = DateTime.UtcNow,
                LastSeenUtc = DateTime.UtcNow,
                SuppressedUtc = DateTime.UtcNow,
                SuppressedBy = "someone",
            };
            await _suppressions.AddAsync(handAuthored);

            var controller = CreateController();
            var result = await controller.Clear("hand-authored-row") as OkObjectResult;

            var dto = Assert.IsType<EmailSuppressionDto>(result!.Value);
            Assert.False(dto.IsActive);
            _reactivation.Verify(
                r => r.ReactivateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never
            );
        }

        [Fact]
        public async Task Clear_ProviderUnsubscribe_WithNoProviderConfigured_ClearsWithoutClaimingReactivation()
        {
            // The webhook-only / Postmark-less registration returns SkippedNotConfigured: sends
            // do not pass through the provider's suppression filter, so nothing blocks the clear
            // - but the audit entry must not claim a reactivation either.
            var seeded = await SeedAsync("jane@example.com", SuppressionReason.ProviderUnsubscribe, new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
            _reactivation
                .Setup(r => r.ReactivateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(PostmarkReactivationResult.NotConfigured);

            NewAuditLogEntry entry = null;
            _auditLog
                .Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>()))
                .Callback<NewAuditLogEntry>(e => entry = e)
                .Returns(Task.CompletedTask);

            var controller = CreateController();
            var result = await controller.Clear(seeded.Id) as OkObjectResult;

            var dto = Assert.IsType<EmailSuppressionDto>(result!.Value);
            Assert.False(dto.IsActive);
            Assert.NotNull(entry);
            Assert.DoesNotContain("reactivat", entry.Action, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Clear_AlreadyClearedProviderUnsubscribe_IsIdempotentWithoutAProviderCall()
        {
            // Under provider-first ordering a cleared record can only exist after a successful
            // (or not-configured) reactivation, so the idempotent re-clear has nothing to do at
            // the provider.
            var seeded = await SeedAsync("jane@example.com", SuppressionReason.ProviderUnsubscribe, new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
            await new EmailSuppressionService(_suppressions, _time).ClearAsync(seeded.Email, "first-admin");

            var controller = CreateController();
            var result = await controller.Clear(seeded.Id) as OkObjectResult;

            var dto = Assert.IsType<EmailSuppressionDto>(result!.Value);
            Assert.False(dto.IsActive);
            Assert.Equal("first-admin", dto.ClearedBy);
            _reactivation.Verify(
                r => r.ReactivateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never
            );
            _auditLog.Verify(r => r.AddAsync(It.IsAny<NewAuditLogEntry>()), Times.Never);
        }
    }
}
