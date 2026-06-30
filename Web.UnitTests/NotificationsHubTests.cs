using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Web.Hubs;
using Web.Models;
using Web.Services;
using Web.Services.Repositories;
using Xunit;

namespace Web.UnitTests;

public sealed class NotificationsHubTests
{
    [Fact]
    public async Task OnConnectedAsync_joins_one_group_per_resolved_audience()
    {
        // Unique id derivation is idProvider + nameId (User.GetUniqueIdFromClaims).
        var admin = new User { UniqueId = "idpu1", Roles = new List<User.Role> { User.Role.Administrator } };
        var userRepo = new Mock<IUserRepository>();
        userRepo.Setup(r => r.GetByUniqueIdAsync("idpu1")).ReturnsAsync(admin);

        var cache = CacheWith(new Committee { Id = "board", ManagementRole = User.Role.Board });
        var groups = new Mock<IGroupManager>();
        var hub = new NotificationsHub(userRepo.Object, cache, NullLogger<NotificationsHub>.Instance)
        {
            Context = ContextFor("u1", "idp"),
            Groups = groups.Object,
        };

        await hub.OnConnectedAsync();

        groups.Verify(
            g => g.AddToGroupAsync("conn-1", NotificationAudience.Administrators, It.IsAny<CancellationToken>()),
            Times.Once);
        groups.Verify(
            g => g.AddToGroupAsync("conn-1", NotificationAudience.Committee("board"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task OnConnectedAsync_joins_no_groups_for_a_user_with_no_audience()
    {
        var resident = new User { UniqueId = "idpu1", Roles = new List<User.Role> { User.Role.Resident } };
        var userRepo = new Mock<IUserRepository>();
        userRepo.Setup(r => r.GetByUniqueIdAsync("idpu1")).ReturnsAsync(resident);

        var cache = CacheWith(new Committee { Id = "board", ManagementRole = User.Role.Board });
        var groups = new Mock<IGroupManager>();
        var hub = new NotificationsHub(userRepo.Object, cache, NullLogger<NotificationsHub>.Instance)
        {
            Context = ContextFor("u1", "idp"),
            Groups = groups.Object,
        };

        await hub.OnConnectedAsync();

        groups.Verify(
            g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task OnConnectedAsync_aborts_the_connection_when_group_resolution_fails()
    {
        var userRepo = new Mock<IUserRepository>();
        userRepo.Setup(r => r.GetByUniqueIdAsync(It.IsAny<string>())).ThrowsAsync(new Exception("cosmos down"));

        var cache = CacheWith();
        var context = new Mock<HubCallerContext>();
        context.Setup(c => c.ConnectionId).Returns("conn-1");
        context.Setup(c => c.User).Returns(Principal("u1", "idp"));
        var hub = new NotificationsHub(userRepo.Object, cache, NullLogger<NotificationsHub>.Instance)
        {
            Context = context.Object,
            Groups = Mock.Of<IGroupManager>(),
        };

        await hub.OnConnectedAsync();

        context.Verify(c => c.Abort(), Times.Once);
    }

    private static CommitteeListCache CacheWith(params Committee[] committees)
    {
        var committeeRepo = new Mock<ICommitteeRepository>();
        committeeRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Committee>(committees));
        return new CommitteeListCache(committeeRepo.Object, new MemoryCache(new MemoryCacheOptions()));
    }

    private static HubCallerContext ContextFor(string nameId, string idp)
    {
        var context = new Mock<HubCallerContext>();
        context.Setup(c => c.ConnectionId).Returns("conn-1");
        context.Setup(c => c.User).Returns(Principal(nameId, idp));
        return context.Object;
    }

    private static ClaimsPrincipal Principal(string nameId, string idp) =>
        new ClaimsPrincipal(new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, nameId),
                new Claim("http://schemas.microsoft.com/identity/claims/identityprovider", idp),
            },
            "Test"));
}
