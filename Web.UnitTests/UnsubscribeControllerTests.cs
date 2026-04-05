using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
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

        private UnsubscribeController CreateController()
        {
            return new UnsubscribeController(_tokenService.Object, _homeRepository.Object, _residentRepository.Object, Mock.Of<ILogger<UnsubscribeController>>());
        }

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
                        SunshineCommitteeEmailOptedIn = allOptedIn
                    }
                }
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
                    SunshineCommitteeEmailOptedIn = true
                },
                Residents = new List<Resident>()
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
            _tokenService.Setup(s => s.ValidateToken("bad")).Returns((UnsubscribeTokenPayload?)null);

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
            _tokenService.Setup(s => s.ValidateToken("tok"))
                .Returns(new UnsubscribeTokenPayload { HomeId = homeId, Email = "j@x.com" });

            var controller = CreateController();
            var result = await controller.OneClickUnsubscribe("unknown", "tok", "One-Click");

            Assert.IsType<BadRequestObjectResult>(result);
        }

        [Fact]
        public async Task OneClickUnsubscribe_HomeNotFound_Returns404()
        {
            var homeId = Guid.NewGuid();
            _tokenService.Setup(s => s.ValidateToken("tok"))
                .Returns(new UnsubscribeTokenPayload { HomeId = homeId, Email = "j@x.com" });
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

            _tokenService.Setup(s => s.ValidateToken("tok"))
                .Returns(new UnsubscribeTokenPayload { HomeId = homeId, Email = email });
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
                case "board": Assert.False(addr.BoardEmailOptedIn); break;
                case "welcome": Assert.False(addr.WelcomeEmailOptedIn); break;
                case "garden": Assert.False(addr.GardenClubEmailOptedIn); break;
                case "social": Assert.False(addr.SocialCommitteeEmailOptedIn); break;
                case "sunshine": Assert.False(addr.SunshineCommitteeEmailOptedIn); break;
            }
        }

        [Fact]
        public async Task OneClickUnsubscribe_EmailNotOnHome_Returns404()
        {
            var homeId = Guid.NewGuid();
            var home = CreateTestHome(homeId, "other@example.com");
            var resident = CreateTestResident(homeId, "other@example.com");

            _tokenService.Setup(s => s.ValidateToken("tok"))
                .Returns(new UnsubscribeTokenPayload { HomeId = homeId, Email = "missing@example.com" });
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

            _tokenService.Setup(s => s.ValidateToken("tok"))
                .Returns(new UnsubscribeTokenPayload { HomeId = homeId, Email = "jane@example.com" });
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
            _tokenService.Setup(s => s.ValidateToken("bad")).Returns((UnsubscribeTokenPayload?)null);

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

            _tokenService.Setup(s => s.ValidateToken("tok"))
                .Returns(new UnsubscribeTokenPayload { HomeId = homeId, Email = email });
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
            _tokenService.Setup(s => s.ValidateToken("bad")).Returns((UnsubscribeTokenPayload?)null);

            var controller = CreateController();
            var result = await controller.UpdatePreferences("bad", new UpdateEmailPreferencesDto());

            Assert.IsType<BadRequestObjectResult>(result);
        }

        [Fact]
        public async Task UpdatePreferences_NullBody_Returns400()
        {
            _tokenService.Setup(s => s.ValidateToken("tok"))
                .Returns(new UnsubscribeTokenPayload { HomeId = Guid.NewGuid(), Email = "x@x.com" });

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

            _tokenService.Setup(s => s.ValidateToken("tok"))
                .Returns(new UnsubscribeTokenPayload { HomeId = homeId, Email = email });
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
                SunshineCommitteeEmailOptedIn = false
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
                    SunshineCommitteeEmailOptedIn = true
                },
                Residents = new List<Resident>()
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
                        SunshineCommitteeEmailOptedIn = true
                    }
                }
            };

            _tokenService.Setup(s => s.ValidateToken("tok"))
                .Returns(new UnsubscribeTokenPayload { HomeId = homeId, Email = email });
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

            _tokenService.Setup(s => s.ValidateToken("tok"))
                .Returns(new UnsubscribeTokenPayload { HomeId = homeId, Email = email });

            // First call returns a home, but upsert throws concurrency conflict
            var home1 = CreateTestHome(homeId, email, allOptedIn: true);
            var home2 = CreateTestHome(homeId, email, allOptedIn: true);
            var resident1 = CreateTestResident(homeId, email, allOptedIn: true);
            var resident2 = CreateTestResident(homeId, email, allOptedIn: true);

            var getCallCount = 0;
            _homeRepository.Setup(r => r.GetByIdAsync(homeId))
                .ReturnsAsync(() => getCallCount++ == 0 ? home1 : home2);

            var residentCallCount = 0;
            _residentRepository.Setup(r => r.GetByHomeIdAsync(homeId))
                .ReturnsAsync(() => new List<Resident> { residentCallCount++ == 0 ? resident1 : resident2 });
            _residentRepository.Setup(r => r.UpsertAsync(It.IsAny<Resident>())).ReturnsAsync((Resident r) => r);

            var upsertCallCount = 0;
            _homeRepository.Setup(r => r.UpsertAsync(It.IsAny<Home>()))
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

            _tokenService.Setup(s => s.ValidateToken("tok"))
                .Returns(new UnsubscribeTokenPayload { HomeId = homeId, Email = email });

            _homeRepository.Setup(r => r.GetByIdAsync(homeId))
                .ReturnsAsync(() => CreateTestHome(homeId, email, allOptedIn: true));

            _residentRepository.Setup(r => r.GetByHomeIdAsync(homeId))
                .ReturnsAsync(() => new List<Resident> { CreateTestResident(homeId, email, allOptedIn: true) });
            _residentRepository.Setup(r => r.UpsertAsync(It.IsAny<Resident>())).ReturnsAsync((Resident r) => r);

            _homeRepository.Setup(r => r.UpsertAsync(It.IsAny<Home>()))
                .ThrowsAsync(new ConcurrencyConflictException("conflict", new Exception()));

            var controller = CreateController();
            var result = await controller.OneClickUnsubscribe("board", "tok", "One-Click");

            Assert.IsType<ConflictObjectResult>(result);
        }
    }
}
