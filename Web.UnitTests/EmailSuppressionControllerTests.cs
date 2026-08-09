using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
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
        }

        private EmailSuppressionController CreateController()
        {
            return new EmailSuppressionController(
                _suppressions,
                new EmailSuppressionService(_suppressions, _time),
                _auditLog.Object,
                new CurrentUserAccessor(_users.Object)
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
    }
}
