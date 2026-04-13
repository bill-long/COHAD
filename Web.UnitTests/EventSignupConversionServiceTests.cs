using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Moq;
using Web.Models;
using Web.Services;
using Web.Services.Repositories;
using Xunit;

namespace Web.UnitTests;

public sealed class EventSignupConversionServiceTests
{
    [Fact]
    public async Task Converts_user_signup_to_home_signup()
    {
        var userUniqueId = "idp1user1";
        var homeId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        var storedEvent = new CommunityEvent
        {
            Id = eventId,
            Title = "Picnic",
            Signups = new List<EventSignup>
            {
                new EventSignup
                {
                    HomeId = Guid.Empty,
                    UserUniqueId = userUniqueId,
                    UserDisplayName = "New Resident",
                    Adults = 2,
                    Children = 1,
                },
            },
        };

        CommunityEvent replaced = null;
        var mockRepo = new Mock<ICommunityEventRepository>();
        mockRepo.Setup(r => r.GetEventsWithUserSignupAsync(userUniqueId)).ReturnsAsync(new List<CommunityEvent> { storedEvent });
        mockRepo
            .Setup(r => r.ReadAsync(eventId))
            .ReturnsAsync(new CommunityEventReadResult { Event = storedEvent, ETag = "\"e1\"" });
        mockRepo
            .Setup(r => r.ReplaceAsync(It.IsAny<CommunityEvent>(), "\"e1\""))
            .Callback<CommunityEvent, string>((e, _) => replaced = e)
            .ReturnsAsync((CommunityEvent e, string _) => e);

        var service = new EventSignupConversionService(mockRepo.Object);
        var count = await service.ConvertUserSignupsToHomeAsync(userUniqueId, homeId, "42 Oak Ave");

        Assert.Equal(1, count);
        Assert.NotNull(replaced);
        var signup = Assert.Single(replaced!.Signups);
        Assert.Equal(homeId, signup.HomeId);
        Assert.Equal("42 Oak Ave", signup.HomeAddress);
        Assert.Null(signup.UserUniqueId);
        Assert.Equal(2, signup.Adults);
        Assert.Equal(1, signup.Children);
    }

    [Fact]
    public async Task Removes_user_signup_when_home_already_signed_up()
    {
        var userUniqueId = "idp1user1";
        var homeId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        var storedEvent = new CommunityEvent
        {
            Id = eventId,
            Title = "Picnic",
            Signups = new List<EventSignup>
            {
                new EventSignup
                {
                    HomeId = homeId,
                    HomeAddress = "42 Oak Ave",
                    UserDisplayName = "Other Resident",
                    Adults = 3,
                    Children = 0,
                },
                new EventSignup
                {
                    HomeId = Guid.Empty,
                    UserUniqueId = userUniqueId,
                    UserDisplayName = "New Resident",
                    Adults = 1,
                    Children = 0,
                },
            },
        };

        CommunityEvent replaced = null;
        var mockRepo = new Mock<ICommunityEventRepository>();
        mockRepo.Setup(r => r.GetEventsWithUserSignupAsync(userUniqueId)).ReturnsAsync(new List<CommunityEvent> { storedEvent });
        mockRepo
            .Setup(r => r.ReadAsync(eventId))
            .ReturnsAsync(new CommunityEventReadResult { Event = storedEvent, ETag = "\"e1\"" });
        mockRepo
            .Setup(r => r.ReplaceAsync(It.IsAny<CommunityEvent>(), "\"e1\""))
            .Callback<CommunityEvent, string>((e, _) => replaced = e)
            .ReturnsAsync((CommunityEvent e, string _) => e);

        var service = new EventSignupConversionService(mockRepo.Object);
        var count = await service.ConvertUserSignupsToHomeAsync(userUniqueId, homeId, "42 Oak Ave");

        Assert.Equal(1, count);
        Assert.NotNull(replaced);
        var signup = Assert.Single(replaced!.Signups);
        Assert.Equal(homeId, signup.HomeId);
        Assert.Equal(3, signup.Adults); // Home's original signup preserved.
    }

    [Fact]
    public async Task Returns_zero_when_no_user_signups_exist()
    {
        var mockRepo = new Mock<ICommunityEventRepository>();
        mockRepo.Setup(r => r.GetEventsWithUserSignupAsync("someuser")).ReturnsAsync(new List<CommunityEvent>());

        var service = new EventSignupConversionService(mockRepo.Object);
        var count = await service.ConvertUserSignupsToHomeAsync("someuser", Guid.NewGuid(), "1 Main St");

        Assert.Equal(0, count);
        mockRepo.Verify(r => r.ReadAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task Skips_events_with_no_matching_user_signup()
    {
        var eventId = Guid.NewGuid();
        var otherUserId = "other-user";

        var storedEvent = new CommunityEvent
        {
            Id = eventId,
            Title = "Picnic",
            Signups = new List<EventSignup>
            {
                new EventSignup
                {
                    HomeId = Guid.Empty,
                    UserUniqueId = otherUserId,
                    Adults = 1,
                },
            },
        };

        var mockRepo = new Mock<ICommunityEventRepository>();
        mockRepo.Setup(r => r.GetEventsWithUserSignupAsync("target-user")).ReturnsAsync(new List<CommunityEvent>());

        var service = new EventSignupConversionService(mockRepo.Object);
        var count = await service.ConvertUserSignupsToHomeAsync("target-user", Guid.NewGuid(), "1 Main St");

        Assert.Equal(0, count);
        mockRepo.Verify(r => r.ReadAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task Converts_signups_across_multiple_events()
    {
        var userUniqueId = "idp1user1";
        var homeId = Guid.NewGuid();
        var eventId1 = Guid.NewGuid();
        var eventId2 = Guid.NewGuid();

        var event1 = new CommunityEvent
        {
            Id = eventId1,
            Title = "Picnic",
            Signups = new List<EventSignup>
            {
                new EventSignup { HomeId = Guid.Empty, UserUniqueId = userUniqueId, Adults = 1 },
            },
        };

        var event2 = new CommunityEvent
        {
            Id = eventId2,
            Title = "BBQ",
            Signups = new List<EventSignup>
            {
                new EventSignup { HomeId = Guid.Empty, UserUniqueId = userUniqueId, Adults = 2 },
            },
        };

        var mockRepo = new Mock<ICommunityEventRepository>();
        mockRepo.Setup(r => r.GetEventsWithUserSignupAsync(userUniqueId)).ReturnsAsync(new List<CommunityEvent> { event1, event2 });
        mockRepo
            .Setup(r => r.ReadAsync(eventId1))
            .ReturnsAsync(new CommunityEventReadResult { Event = event1, ETag = "\"e1\"" });
        mockRepo
            .Setup(r => r.ReadAsync(eventId2))
            .ReturnsAsync(new CommunityEventReadResult { Event = event2, ETag = "\"e2\"" });
        mockRepo
            .Setup(r => r.ReplaceAsync(It.IsAny<CommunityEvent>(), It.IsAny<string>()))
            .ReturnsAsync((CommunityEvent e, string _) => e);

        var service = new EventSignupConversionService(mockRepo.Object);
        var count = await service.ConvertUserSignupsToHomeAsync(userUniqueId, homeId, "42 Oak Ave");

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task MigrateAll_converts_signups_for_users_with_homes()
    {
        var userId = "idp1user1";
        var homeId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        var storedEvent = new CommunityEvent
        {
            Id = eventId,
            Title = "Picnic",
            Signups = new List<EventSignup>
            {
                new EventSignup
                {
                    HomeId = Guid.Empty,
                    UserUniqueId = userId,
                    UserDisplayName = "New Resident",
                    Adults = 2,
                },
            },
        };

        CommunityEvent replaced = null;
        var mockRepo = new Mock<ICommunityEventRepository>();
        mockRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<CommunityEvent> { storedEvent });
        mockRepo
            .Setup(r => r.ReadAsync(eventId))
            .ReturnsAsync(new CommunityEventReadResult { Event = storedEvent, ETag = "\"e1\"" });
        mockRepo
            .Setup(r => r.ReplaceAsync(It.IsAny<CommunityEvent>(), "\"e1\""))
            .Callback<CommunityEvent, string>((e, _) => replaced = e)
            .ReturnsAsync((CommunityEvent e, string _) => e);

        var mockUsers = new Mock<IUserRepository>();
        mockUsers
            .Setup(r => r.GetByUniqueIdAsync(userId))
            .ReturnsAsync(new User { UniqueId = userId, OwnedHomeIds = new List<Guid> { homeId } });

        var mockHomes = new Mock<IHomeRepository>();
        mockHomes
            .Setup(r => r.GetByIdsAsync(It.IsAny<List<Guid>>()))
            .ReturnsAsync(new List<Home> { new Home { Id = homeId, StreetNumber = 42, StreetName = "Oak Ave" } });

        var service = new EventSignupConversionService(mockRepo.Object);
        var result = await service.MigrateAllUserSignupsAsync(mockUsers.Object, mockHomes.Object);

        Assert.Equal(1, result.SignupsConverted);
        Assert.Equal(0, result.SignupsRemoved);
        Assert.NotNull(replaced);
        var signup = Assert.Single(replaced!.Signups);
        Assert.Equal(homeId, signup.HomeId);
        Assert.Equal("42 Oak Ave", signup.HomeAddress);
        Assert.Null(signup.UserUniqueId);
    }

    [Fact]
    public async Task MigrateAll_skips_users_without_homes()
    {
        var userId = "idp1user1";
        var eventId = Guid.NewGuid();

        var storedEvent = new CommunityEvent
        {
            Id = eventId,
            Title = "Picnic",
            Signups = new List<EventSignup>
            {
                new EventSignup { HomeId = Guid.Empty, UserUniqueId = userId, Adults = 1 },
            },
        };

        var mockRepo = new Mock<ICommunityEventRepository>();
        mockRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<CommunityEvent> { storedEvent });

        var mockUsers = new Mock<IUserRepository>();
        mockUsers
            .Setup(r => r.GetByUniqueIdAsync(userId))
            .ReturnsAsync(new User { UniqueId = userId, OwnedHomeIds = new List<Guid>() });

        var mockHomes = new Mock<IHomeRepository>();
        mockHomes.Setup(r => r.GetByIdsAsync(It.IsAny<List<Guid>>())).ReturnsAsync(new List<Home>());

        var service = new EventSignupConversionService(mockRepo.Object);
        var result = await service.MigrateAllUserSignupsAsync(mockUsers.Object, mockHomes.Object);

        Assert.Equal(0, result.SignupsConverted);
        Assert.Equal(1, result.SignupsSkipped);
        mockRepo.Verify(r => r.ReadAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task MigrateAll_removes_user_signup_when_home_already_signed_up()
    {
        var userId = "idp1user1";
        var homeId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        var storedEvent = new CommunityEvent
        {
            Id = eventId,
            Title = "Picnic",
            Signups = new List<EventSignup>
            {
                new EventSignup { HomeId = homeId, HomeAddress = "42 Oak Ave", Adults = 3 },
                new EventSignup { HomeId = Guid.Empty, UserUniqueId = userId, Adults = 1 },
            },
        };

        CommunityEvent replaced = null;
        var mockRepo = new Mock<ICommunityEventRepository>();
        mockRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<CommunityEvent> { storedEvent });
        mockRepo
            .Setup(r => r.ReadAsync(eventId))
            .ReturnsAsync(new CommunityEventReadResult { Event = storedEvent, ETag = "\"e1\"" });
        mockRepo
            .Setup(r => r.ReplaceAsync(It.IsAny<CommunityEvent>(), "\"e1\""))
            .Callback<CommunityEvent, string>((e, _) => replaced = e)
            .ReturnsAsync((CommunityEvent e, string _) => e);

        var mockUsers = new Mock<IUserRepository>();
        mockUsers
            .Setup(r => r.GetByUniqueIdAsync(userId))
            .ReturnsAsync(new User { UniqueId = userId, OwnedHomeIds = new List<Guid> { homeId } });

        var mockHomes = new Mock<IHomeRepository>();
        mockHomes
            .Setup(r => r.GetByIdsAsync(It.IsAny<List<Guid>>()))
            .ReturnsAsync(new List<Home> { new Home { Id = homeId, StreetNumber = 42, StreetName = "Oak Ave" } });

        var service = new EventSignupConversionService(mockRepo.Object);
        var result = await service.MigrateAllUserSignupsAsync(mockUsers.Object, mockHomes.Object);

        Assert.Equal(0, result.SignupsConverted);
        Assert.Equal(1, result.SignupsRemoved);
        Assert.NotNull(replaced);
        var signup = Assert.Single(replaced!.Signups);
        Assert.Equal(homeId, signup.HomeId);
        Assert.Equal(3, signup.Adults); // Home's original signup preserved.
    }
}
