using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using Web.Authorization;
using Web.Models;
using Web.Services.Repositories;
using Xunit;

namespace Web.UnitTests;

public sealed class RoleAuthorizationHandlerTests
{
    private const string IdentityProviderClaim = "http://schemas.microsoft.com/identity/claims/identityprovider";

    private static ClaimsPrincipal Principal(string nameId, string idp) =>
        new ClaimsPrincipal(
            new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, nameId), new Claim(IdentityProviderClaim, idp) },
                "Test"
            )
        );

    [Fact]
    public async Task Does_not_succeed_when_local_account_not_in_database()
    {
        var user = new ClaimsPrincipal(
            new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "only-name-id") }, "Test")
        );
        var mockRepo = new Mock<IUserRepository>(MockBehavior.Strict);
        mockRepo.Setup(r => r.GetByUniqueIdAsync("localonly-name-id")).ReturnsAsync((User?)null);
        var handler = new RoleAuthorizationHandler(mockRepo.Object, Mock.Of<ILogger<RoleAuthorizationHandler>>());
        var requirement = new RoleAuthorizationRequirement(User.Role.Resident);
        var context = new AuthorizationHandlerContext(
            new IAuthorizationRequirement[] { requirement },
            user,
            resource: null
        );

        await ((IAuthorizationHandler)handler).HandleAsync(context);

        Assert.False(context.HasSucceeded);
        mockRepo.Verify(r => r.GetByUniqueIdAsync("localonly-name-id"), Times.Once);
    }

    [Fact]
    public async Task Does_not_succeed_when_user_not_in_database()
    {
        var uniqueId = "google.comu1";
        var mockRepo = new Mock<IUserRepository>();
        mockRepo.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync((User?)null);
        var handler = new RoleAuthorizationHandler(mockRepo.Object, Mock.Of<ILogger<RoleAuthorizationHandler>>());
        var requirement = new RoleAuthorizationRequirement(User.Role.Resident);
        var context = new AuthorizationHandlerContext(
            new IAuthorizationRequirement[] { requirement },
            Principal("u1", "google.com"),
            resource: null
        );

        await ((IAuthorizationHandler)handler).HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task Does_not_succeed_when_user_has_null_roles()
    {
        var uniqueId = "google.comu1";
        var mockRepo = new Mock<IUserRepository>();
        mockRepo
            .Setup(r => r.GetByUniqueIdAsync(uniqueId))
            .ReturnsAsync(new User { UniqueId = uniqueId, Roles = null });
        var handler = new RoleAuthorizationHandler(mockRepo.Object, Mock.Of<ILogger<RoleAuthorizationHandler>>());
        var requirement = new RoleAuthorizationRequirement(User.Role.Resident);
        var context = new AuthorizationHandlerContext(
            new IAuthorizationRequirement[] { requirement },
            Principal("u1", "google.com"),
            resource: null
        );

        await ((IAuthorizationHandler)handler).HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task Does_not_succeed_when_role_missing()
    {
        var uniqueId = "google.comu1";
        var mockRepo = new Mock<IUserRepository>();
        mockRepo
            .Setup(r => r.GetByUniqueIdAsync(uniqueId))
            .ReturnsAsync(
                new User
                {
                    UniqueId = uniqueId,
                    Roles = new List<User.Role> { User.Role.Board },
                }
            );
        var handler = new RoleAuthorizationHandler(mockRepo.Object, Mock.Of<ILogger<RoleAuthorizationHandler>>());
        var requirement = new RoleAuthorizationRequirement(User.Role.Administrator);
        var context = new AuthorizationHandlerContext(
            new IAuthorizationRequirement[] { requirement },
            Principal("u1", "google.com"),
            resource: null
        );

        await ((IAuthorizationHandler)handler).HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task Succeeds_for_Resident_requirement_when_user_has_Administrator_only()
    {
        var uniqueId = "google.comu1";
        var mockRepo = new Mock<IUserRepository>();
        mockRepo
            .Setup(r => r.GetByUniqueIdAsync(uniqueId))
            .ReturnsAsync(
                new User
                {
                    UniqueId = uniqueId,
                    Roles = new List<User.Role> { User.Role.Administrator },
                }
            );
        var handler = new RoleAuthorizationHandler(mockRepo.Object, Mock.Of<ILogger<RoleAuthorizationHandler>>());
        var requirement = new RoleAuthorizationRequirement(User.Role.Resident);
        var context = new AuthorizationHandlerContext(
            new IAuthorizationRequirement[] { requirement },
            Principal("u1", "google.com"),
            resource: null
        );

        await ((IAuthorizationHandler)handler).HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task Succeeds_when_user_has_required_role()
    {
        var uniqueId = "google.comu1";
        var mockRepo = new Mock<IUserRepository>();
        mockRepo
            .Setup(r => r.GetByUniqueIdAsync(uniqueId))
            .ReturnsAsync(
                new User
                {
                    UniqueId = uniqueId,
                    Roles = new List<User.Role> { User.Role.Resident, User.Role.Administrator },
                }
            );
        var handler = new RoleAuthorizationHandler(mockRepo.Object, Mock.Of<ILogger<RoleAuthorizationHandler>>());
        var requirement = new RoleAuthorizationRequirement(User.Role.Administrator);
        var context = new AuthorizationHandlerContext(
            new IAuthorizationRequirement[] { requirement },
            Principal("u1", "google.com"),
            resource: null
        );

        await ((IAuthorizationHandler)handler).HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    // ── AuthorizedUserCache population ──
    // The endpoints reuse the user resolved here instead of reading it again, so these lock the
    // handoff itself: the hand-injected cache entry in EmailControllerJobTests would pass whether or
    // not the handlers ever store anything.

    [Fact]
    public async Task Succeeding_stores_the_resolved_user_for_the_endpoint()
    {
        var uniqueId = "google.comu1";
        var stored = new User { UniqueId = uniqueId, Roles = new List<User.Role> { User.Role.Resident } };
        var mockRepo = new Mock<IUserRepository>();
        mockRepo.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(stored);
        var handler = new RoleAuthorizationHandler(mockRepo.Object, Mock.Of<ILogger<RoleAuthorizationHandler>>());
        var httpContext = new DefaultHttpContext();
        var requirement = new RoleAuthorizationRequirement(User.Role.Resident);
        var context = new AuthorizationHandlerContext(
            new IAuthorizationRequirement[] { requirement },
            Principal("u1", "google.com"),
            resource: httpContext
        );

        await ((IAuthorizationHandler)handler).HandleAsync(context);

        Assert.True(context.HasSucceeded);
        Assert.Same(stored, AuthorizedUserCache.Get(httpContext));
    }

    [Fact]
    public async Task Failing_stores_nothing_for_the_endpoint()
    {
        var uniqueId = "google.comu1";
        var mockRepo = new Mock<IUserRepository>();
        mockRepo
            .Setup(r => r.GetByUniqueIdAsync(uniqueId))
            .ReturnsAsync(new User { UniqueId = uniqueId, Roles = new List<User.Role> { User.Role.Resident } });
        var handler = new RoleAuthorizationHandler(mockRepo.Object, Mock.Of<ILogger<RoleAuthorizationHandler>>());
        var httpContext = new DefaultHttpContext();
        var requirement = new RoleAuthorizationRequirement(User.Role.Administrator);
        var context = new AuthorizationHandlerContext(
            new IAuthorizationRequirement[] { requirement },
            Principal("u1", "google.com"),
            resource: httpContext
        );

        await ((IAuthorizationHandler)handler).HandleAsync(context);

        Assert.False(context.HasSucceeded);
        Assert.Null(AuthorizedUserCache.Get(httpContext));
    }

    [Fact]
    public async Task Succeeding_on_a_non_http_resource_is_harmless()
    {
        // The same policies guard the SignalR hubs, where the resource is a HubInvocationContext.
        // Nothing is cached there; the endpoints fall back to their own read.
        var uniqueId = "google.comu1";
        var mockRepo = new Mock<IUserRepository>();
        mockRepo
            .Setup(r => r.GetByUniqueIdAsync(uniqueId))
            .ReturnsAsync(new User { UniqueId = uniqueId, Roles = new List<User.Role> { User.Role.Resident } });
        var handler = new RoleAuthorizationHandler(mockRepo.Object, Mock.Of<ILogger<RoleAuthorizationHandler>>());
        var requirement = new RoleAuthorizationRequirement(User.Role.Resident);
        var context = new AuthorizationHandlerContext(
            new IAuthorizationRequirement[] { requirement },
            Principal("u1", "google.com"),
            resource: new object()
        );

        await ((IAuthorizationHandler)handler).HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }
}
