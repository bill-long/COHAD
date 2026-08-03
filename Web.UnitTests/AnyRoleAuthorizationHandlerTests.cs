using System.Collections.Generic;
using System.Linq;
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
/// Guards every "EmailSender" endpoint and the CommitteeEditor hub. Its identity derivation moved
/// from a throwing parse to the accessor's null-returning one, so the deny paths are locked here.
/// </summary>
public sealed class AnyRoleAuthorizationHandlerTests
{
    private const string IdentityProviderClaim = "http://schemas.microsoft.com/identity/claims/identityprovider";

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));

    private static ClaimsPrincipal FullPrincipal() =>
        Principal(new Claim(ClaimTypes.NameIdentifier, "u1"), new Claim(IdentityProviderClaim, "google.com"));

    private static (AnyRoleAuthorizationHandler Handler, Mock<IUserRepository> Repo) HandlerFor(User user)
    {
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByUniqueIdAsync(It.IsAny<string>())).ReturnsAsync(user);
        var handler = new AnyRoleAuthorizationHandler(
            new CurrentUserAccessor(repo.Object),
            Mock.Of<ILogger<AnyRoleAuthorizationHandler>>()
        );
        return (handler, repo);
    }

    private static async Task<AuthorizationHandlerContext> EvaluateAsync(
        AnyRoleAuthorizationHandler handler,
        ClaimsPrincipal principal,
        params User.Role[] required
    )
    {
        var requirement = new AnyRoleAuthorizationRequirement(required);
        var context = new AuthorizationHandlerContext(
            new IAuthorizationRequirement[] { requirement },
            principal,
            resource: null
        );
        await ((IAuthorizationHandler)handler).HandleAsync(context);
        return context;
    }

    [Fact]
    public async Task Succeeds_when_the_user_holds_any_one_of_the_required_roles()
    {
        var (handler, _) = HandlerFor(new User { UniqueId = "google.comu1", Roles = new List<User.Role> { User.Role.GardenClub } });

        var context = await EvaluateAsync(handler, FullPrincipal(), User.Role.Administrator, User.Role.GardenClub);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task Does_not_succeed_when_the_user_holds_none_of_them()
    {
        var (handler, _) = HandlerFor(new User { UniqueId = "google.comu1", Roles = new List<User.Role> { User.Role.Resident } });

        var context = await EvaluateAsync(handler, FullPrincipal(), User.Role.Administrator, User.Role.GardenClub);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task Does_not_succeed_for_a_token_with_no_name_identifier()
    {
        // The derivation used to throw here; it now returns null, so this is the path that would
        // silently admit a claim-less token if the null were not checked.
        var (handler, repo) = HandlerFor(new User { UniqueId = "google.comu1", Roles = new List<User.Role> { User.Role.GardenClub } });

        var context = await EvaluateAsync(handler, Principal(new Claim("unrelated", "x")), User.Role.GardenClub);

        Assert.False(context.HasSucceeded);
        repo.Verify(r => r.GetByUniqueIdAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Does_not_succeed_when_no_user_document_matches()
    {
        var (handler, _) = HandlerFor(null);

        var context = await EvaluateAsync(handler, FullPrincipal(), User.Role.GardenClub);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task Does_not_succeed_when_the_user_has_null_roles()
    {
        var (handler, _) = HandlerFor(new User { UniqueId = "google.comu1", Roles = null });

        var context = await EvaluateAsync(handler, FullPrincipal(), User.Role.GardenClub);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task Reads_the_user_through_the_accessor_so_the_endpoint_reuses_it()
    {
        var stored = new User { UniqueId = "google.comu1", Roles = new List<User.Role> { User.Role.GardenClub } };
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByUniqueIdAsync(It.IsAny<string>())).ReturnsAsync(stored);
        var accessor = new CurrentUserAccessor(repo.Object);
        var handler = new AnyRoleAuthorizationHandler(accessor, Mock.Of<ILogger<AnyRoleAuthorizationHandler>>());

        await EvaluateAsync(handler, FullPrincipal(), User.Role.GardenClub);
        Assert.Same(stored, await accessor.GetAsync(FullPrincipal()));

        repo.Verify(r => r.GetByUniqueIdAsync(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public void The_requirement_does_not_alias_the_array_it_is_given()
    {
        // It decides access for the process's lifetime, so a caller must not be able to rewrite the
        // role set through the array it passed in.
        var roles = new[] { User.Role.GardenClub };
        var requirement = new AnyRoleAuthorizationRequirement(roles);

        roles[0] = User.Role.Administrator;

        Assert.Equal(new[] { User.Role.GardenClub }, requirement.RequiredRoles);
    }

    [Fact]
    public void The_EmailSender_role_set_is_exactly_these_roles()
    {
        // The behaviour theories derive their cases from this set, so they cannot notice it growing.
        // This is the one place that says which roles the policy admits - and, by omission, which it
        // does not: Resident, ArchitecturalCommittee and LandscapeCommittee are deliberately absent,
        // and adding one here means deciding that role may read every email job.
        Assert.Equal(
            new[]
            {
                User.Role.Administrator,
                User.Role.Board,
                User.Role.WelcomeCommittee,
                User.Role.GardenClub,
                User.Role.SocialCommittee,
                User.Role.SunshineCommittee,
            },
            AuthorizationRoleSets.EmailSender.ToArray()
        );
    }

    [Fact]
    public void The_CommitteeEditor_role_set_is_exactly_these_roles()
    {
        // Same reason as the EmailSender set, plus one this list carries alone: it has to stay in
        // step with the frontend's rolePermissions.manageCommitteesRoles, which nothing compiles
        // against. Changing either side without the other silently disagrees about who may manage
        // a committee, so this is the place that says what the backend admits.
        Assert.Equal(
            new[]
            {
                User.Role.Administrator,
                User.Role.Board,
                User.Role.WelcomeCommittee,
                User.Role.GardenClub,
                User.Role.SocialCommittee,
                User.Role.SunshineCommittee,
                User.Role.ArchitecturalCommittee,
                User.Role.LandscapeCommittee,
            },
            AuthorizationRoleSets.CommitteeEditor.ToArray()
        );
    }
}
