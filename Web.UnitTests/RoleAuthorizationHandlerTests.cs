using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
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
        new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, nameId),
            new Claim(IdentityProviderClaim, idp)
        }, "Test"));

    [Fact]
    public async Task Does_not_succeed_when_unique_id_cannot_be_built_from_claims()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "only-name-id")
        }, "Test"));
        var mockRepo = new Mock<IUserRepository>(MockBehavior.Strict);
        var handler = new RoleAuthorizationHandler(mockRepo.Object, Mock.Of<ILogger<RoleAuthorizationHandler>>());
        var requirement = new RoleAuthorizationRequirement(User.Role.Resident);
        var context = new AuthorizationHandlerContext(new IAuthorizationRequirement[] { requirement }, user, resource: null);

        await ((IAuthorizationHandler)handler).HandleAsync(context);

        Assert.False(context.HasSucceeded);
        mockRepo.Verify(r => r.GetByUniqueIdAsync(It.IsAny<string>()), Times.Never);
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
            resource: null);

        await ((IAuthorizationHandler)handler).HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task Does_not_succeed_when_user_has_null_roles()
    {
        var uniqueId = "google.comu1";
        var mockRepo = new Mock<IUserRepository>();
        mockRepo.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(new User
        {
            UniqueId = uniqueId,
            Roles = null
        });
        var handler = new RoleAuthorizationHandler(mockRepo.Object, Mock.Of<ILogger<RoleAuthorizationHandler>>());
        var requirement = new RoleAuthorizationRequirement(User.Role.Resident);
        var context = new AuthorizationHandlerContext(
            new IAuthorizationRequirement[] { requirement },
            Principal("u1", "google.com"),
            resource: null);

        await ((IAuthorizationHandler)handler).HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task Does_not_succeed_when_role_missing()
    {
        var uniqueId = "google.comu1";
        var mockRepo = new Mock<IUserRepository>();
        mockRepo.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(new User
        {
            UniqueId = uniqueId,
            Roles = new List<User.Role> { User.Role.Board }
        });
        var handler = new RoleAuthorizationHandler(mockRepo.Object, Mock.Of<ILogger<RoleAuthorizationHandler>>());
        var requirement = new RoleAuthorizationRequirement(User.Role.Administrator);
        var context = new AuthorizationHandlerContext(
            new IAuthorizationRequirement[] { requirement },
            Principal("u1", "google.com"),
            resource: null);

        await ((IAuthorizationHandler)handler).HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task Succeeds_for_Resident_requirement_when_user_has_Administrator_only()
    {
        var uniqueId = "google.comu1";
        var mockRepo = new Mock<IUserRepository>();
        mockRepo.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(new User
        {
            UniqueId = uniqueId,
            Roles = new List<User.Role> { User.Role.Administrator }
        });
        var handler = new RoleAuthorizationHandler(mockRepo.Object, Mock.Of<ILogger<RoleAuthorizationHandler>>());
        var requirement = new RoleAuthorizationRequirement(User.Role.Resident);
        var context = new AuthorizationHandlerContext(
            new IAuthorizationRequirement[] { requirement },
            Principal("u1", "google.com"),
            resource: null);

        await ((IAuthorizationHandler)handler).HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task Succeeds_when_user_has_required_role()
    {
        var uniqueId = "google.comu1";
        var mockRepo = new Mock<IUserRepository>();
        mockRepo.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(new User
        {
            UniqueId = uniqueId,
            Roles = new List<User.Role> { User.Role.Resident, User.Role.Administrator }
        });
        var handler = new RoleAuthorizationHandler(mockRepo.Object, Mock.Of<ILogger<RoleAuthorizationHandler>>());
        var requirement = new RoleAuthorizationRequirement(User.Role.Administrator);
        var context = new AuthorizationHandlerContext(
            new IAuthorizationRequirement[] { requirement },
            Principal("u1", "google.com"),
            resource: null);

        await ((IAuthorizationHandler)handler).HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }
}
