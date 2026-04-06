using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Web.Models;
using Web.Services;
using Web.Services.Repositories;
using Xunit;

namespace Web.UnitTests
{
    public class EmailDeliveryActionServiceTests
    {
        private readonly Mock<IHomeRepository> _homes = new();
        private readonly Mock<IResidentRepository> _residents = new();
        private readonly Mock<IAuditLogRepository> _auditLog = new();

        private EmailDeliveryActionService CreateService()
        {
            return new EmailDeliveryActionService(
                _homes.Object,
                _residents.Object,
                _auditLog.Object,
                NullLogger<EmailDeliveryActionService>.Instance
            );
        }

        private void SetupHomes(params Home[] homes)
        {
            _homes.Setup(r => r.GetAllAsync()).ReturnsAsync(homes.ToList());
        }

        private void SetupResidents(params Resident[] residents)
        {
            _residents.Setup(r => r.GetAllAsync()).ReturnsAsync(residents.ToList());
        }

        // ─── Bounce opt-out ───

        [Fact]
        public async Task Bounce_OptsOutMatchingHomeEmail()
        {
            var home = new Home
            {
                Id = Guid.NewGuid(),
                EmailAddress = new EmailAddress
                {
                    Address = "bounce@example.com",
                    BoardEmailOptedIn = true,
                    WelcomeEmailOptedIn = true,
                    GardenClubEmailOptedIn = true,
                    SocialCommitteeEmailOptedIn = true,
                    SunshineCommitteeEmailOptedIn = true,
                },
            };
            SetupHomes(home);
            SetupResidents();

            var service = CreateService();
            await service.ProcessDeliveryEventAsync("bounce@example.com", DeliveryStatus.Bounced, null);

            Assert.False(home.EmailAddress.BoardEmailOptedIn);
            Assert.False(home.EmailAddress.WelcomeEmailOptedIn);
            Assert.False(home.EmailAddress.GardenClubEmailOptedIn);
            Assert.False(home.EmailAddress.SocialCommitteeEmailOptedIn);
            Assert.False(home.EmailAddress.SunshineCommitteeEmailOptedIn);
            _homes.Verify(r => r.UpsertAsync(home), Times.Once);
            _auditLog.Verify(r => r.AddAsync(It.IsAny<NewAuditLogEntry>()), Times.Once);
        }

        [Fact]
        public async Task Bounce_OptsOutMatchingResidentEmail()
        {
            SetupHomes();
            var resident = new Resident
            {
                Id = Guid.NewGuid(),
                HomeId = Guid.NewGuid(),
                EmailAddresses = new List<EmailAddress>
                {
                    new()
                    {
                        Address = "bounce@example.com",
                        BoardEmailOptedIn = true,
                        WelcomeEmailOptedIn = true,
                    },
                    new() { Address = "other@example.com", BoardEmailOptedIn = true },
                },
            };
            SetupResidents(resident);

            var service = CreateService();
            await service.ProcessDeliveryEventAsync("bounce@example.com", DeliveryStatus.Bounced, null);

            // Only the matching email should be opted out
            Assert.False(resident.EmailAddresses[0].BoardEmailOptedIn);
            Assert.False(resident.EmailAddresses[0].WelcomeEmailOptedIn);
            Assert.True(resident.EmailAddresses[1].BoardEmailOptedIn); // Untouched
            _residents.Verify(r => r.UpsertAsync(resident), Times.Once);
        }

        // ─── Spam report ───

        [Fact]
        public async Task SpamReport_OptsOutMatchingEmail()
        {
            var home = new Home
            {
                Id = Guid.NewGuid(),
                EmailAddress = new EmailAddress { Address = "spam@example.com", BoardEmailOptedIn = true },
            };
            SetupHomes(home);
            SetupResidents();

            var service = CreateService();
            await service.ProcessDeliveryEventAsync("spam@example.com", DeliveryStatus.SpamReport, null);

            Assert.False(home.EmailAddress.BoardEmailOptedIn);
            _homes.Verify(r => r.UpsertAsync(home), Times.Once);
        }

        // ─── No action for non-terminal statuses ───

        [Theory]
        [InlineData(DeliveryStatus.Delivered)]
        [InlineData(DeliveryStatus.Deferred)]
        [InlineData(DeliveryStatus.Unknown)]
        public async Task NonTerminalStatus_TakesNoAction(DeliveryStatus status)
        {
            var service = CreateService();
            await service.ProcessDeliveryEventAsync("user@example.com", status, null);

            _homes.Verify(r => r.GetAllAsync(), Times.Never);
            _residents.Verify(r => r.GetAllAsync(), Times.Never);
        }

        // ─── Case-insensitive matching ───

        [Fact]
        public async Task Bounce_MatchesCaseInsensitive()
        {
            var home = new Home
            {
                Id = Guid.NewGuid(),
                EmailAddress = new EmailAddress { Address = "User@Example.COM", BoardEmailOptedIn = true },
            };
            SetupHomes(home);
            SetupResidents();

            var service = CreateService();
            await service.ProcessDeliveryEventAsync("user@example.com", DeliveryStatus.Bounced, null);

            Assert.False(home.EmailAddress.BoardEmailOptedIn);
        }

        // ─── No matches ───

        [Fact]
        public async Task NoMatchingEmails_SkipsAuditLog()
        {
            SetupHomes();
            SetupResidents();

            var service = CreateService();
            await service.ProcessDeliveryEventAsync("nobody@example.com", DeliveryStatus.Bounced, null);

            _auditLog.Verify(r => r.AddAsync(It.IsAny<NewAuditLogEntry>()), Times.Never);
        }

        // ─── Email redaction ───

        [Theory]
        [InlineData("alice@example.com", "ali***@example.com")]
        [InlineData("ab@example.com", "ab***@example.com")]
        [InlineData("a@x.com", "a***@x.com")]
        [InlineData("longuser@domain.org", "lon***@domain.org")]
        public void RedactEmail_ShowsPartialLocalPart(string input, string expected)
        {
            Assert.Equal(expected, EmailDeliveryActionService.RedactEmail(input));
        }
    }
}
