using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using Moq;
using Web.Authorization;
using Web.Models;
using Web.Services;
using Web.Services.Repositories;
using Xunit;

namespace Web.UnitTests;

/// <summary>
/// The accessor exists to collapse the two reads of the caller's user document that every
/// role-gated request used to make - once in authorization, once in the endpoint. These lock that
/// it reads once, that it reads for the right person, and that an unusable token costs no read.
/// </summary>
public sealed class CurrentUserAccessorTests
{
    private const string IdentityProviderClaim = "http://schemas.microsoft.com/identity/claims/identityprovider";

    private static ClaimsPrincipal Principal(string nameId = "u1", string idp = "google.com") =>
        new ClaimsPrincipal(
            new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, nameId), new Claim(IdentityProviderClaim, idp) },
                "Test"
            )
        );

    private static Mock<IUserRepository> RepoReturning(User user)
    {
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByUniqueIdAsync(It.IsAny<string>())).ReturnsAsync(user);
        return repo;
    }

    [Fact]
    public async Task Reads_the_user_once_per_request_however_many_callers_ask()
    {
        var stored = new User { UniqueId = "google.comu1" };
        var repo = RepoReturning(stored);
        var accessor = new CurrentUserAccessor(repo.Object);

        var first = await accessor.GetAsync(Principal());
        var second = await accessor.GetAsync(Principal());

        Assert.Same(stored, first);
        Assert.Same(stored, second);
        repo.Verify(r => r.GetByUniqueIdAsync("google.comu1"), Times.Once);
    }

    [Fact]
    public async Task Concurrent_callers_share_one_read()
    {
        // The task is cached rather than its result, so authorization and an endpoint that both ask
        // before either completes do not race into two reads.
        var stored = new User { UniqueId = "google.comu1" };
        var repo = RepoReturning(stored);
        var accessor = new CurrentUserAccessor(repo.Object);

        var results = await Task.WhenAll(accessor.GetAsync(Principal()), accessor.GetAsync(Principal()));

        Assert.All(results, u => Assert.Same(stored, u));
        repo.Verify(r => r.GetByUniqueIdAsync(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task A_different_principal_is_looked_up_again()
    {
        // Returning the first caller's user to a second identity would be an authorization bug, not
        // a stale cache, so the memo is keyed on the identity it was made for.
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByUniqueIdAsync("google.comu1")).ReturnsAsync(new User { UniqueId = "google.comu1" });
        repo.Setup(r => r.GetByUniqueIdAsync("google.comu2")).ReturnsAsync(new User { UniqueId = "google.comu2" });
        var accessor = new CurrentUserAccessor(repo.Object);

        var first = await accessor.GetAsync(Principal("u1"));
        var second = await accessor.GetAsync(Principal("u2"));

        Assert.Equal("google.comu1", first!.UniqueId);
        Assert.Equal("google.comu2", second!.UniqueId);
    }

    [Fact]
    public async Task An_unauthenticated_principal_costs_no_read()
    {
        // Anonymous endpoints call this to find out whether anyone is signed in; that question must
        // not turn into a Cosmos point read.
        var repo = RepoReturning(new User());
        var accessor = new CurrentUserAccessor(repo.Object);

        var anonymous = new ClaimsPrincipal(new ClaimsIdentity()); // no authentication type
        Assert.Null(await accessor.GetAsync(anonymous));
        Assert.Null(await accessor.GetAsync(null));

        repo.Verify(r => r.GetByUniqueIdAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task A_token_missing_the_required_claims_yields_null_rather_than_throwing()
    {
        // GetUniqueIdFromClaims throws when the identity-provider claim is absent. A partially
        // claimed token is a request to reject, not an exception to surface.
        var repo = RepoReturning(new User());
        var accessor = new CurrentUserAccessor(repo.Object);
        var partial = new ClaimsPrincipal(
            new ClaimsIdentity(new[] { new Claim("unrelated", "value") }, "Test")
        );

        Assert.Null(await accessor.GetAsync(partial));
        repo.Verify(r => r.GetByUniqueIdAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Authorization_and_the_endpoint_that_follows_it_share_one_read()
    {
        // The reason this type exists. Before it, the policy read the user document and then the
        // endpoint read the same document again - two point reads of one item per request.
        var stored = new User { UniqueId = "google.comu1", Roles = new List<User.Role> { User.Role.Resident } };
        var repo = RepoReturning(stored);
        var accessor = new CurrentUserAccessor(repo.Object);

        var handler = new RoleAuthorizationHandler(accessor, Mock.Of<ILogger<RoleAuthorizationHandler>>());
        var requirement = new RoleAuthorizationRequirement(User.Role.Resident);
        var context = new AuthorizationHandlerContext(
            new IAuthorizationRequirement[] { requirement },
            Principal(),
            resource: null
        );

        await ((IAuthorizationHandler)handler).HandleAsync(context);
        Assert.True(context.HasSucceeded);

        // ...and now the endpoint asks for the same caller.
        Assert.Same(stored, await accessor.GetAsync(Principal()));
        repo.Verify(r => r.GetByUniqueIdAsync(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task A_caller_with_no_user_document_yields_null()
    {
        var repo = RepoReturning(null);
        var accessor = new CurrentUserAccessor(repo.Object);

        Assert.Null(await accessor.GetAsync(Principal()));
    }
}
