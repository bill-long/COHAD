using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Web.Controllers;
using Web.Models;
using Web.Services;
using Web.Services.Repositories;
using Xunit;

namespace Web.UnitTests;

public sealed class MeControllerTests
{
    private const string IdentityProviderClaim = "http://schemas.microsoft.com/identity/claims/identityprovider";

    private static MeController CreateController(
        IUserRepository userRepository,
        IHomeRepository homeRepository,
        IEmailService emailService,
        string nameId = "u1",
        string idp = "google.com")
    {
        var controller = new MeController(userRepository, homeRepository, emailService, Mock.Of<ILogger<MeController>>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.NameIdentifier, nameId),
                        new Claim(ClaimTypes.GivenName, "Test"),
                        new Claim(ClaimTypes.Surname, "User"),
                        new Claim(IdentityProviderClaim, idp),
                        new Claim("emails", "test@example.com")
                    }, "Test"))
                }
            }
        };
        return controller;
    }

    [Fact]
    public async Task Get_existing_user_returns_without_waiting_for_last_login_upsert()
    {
        var uniqueId = "google.comu1";
        var users = new Mock<IUserRepository>();
        users.Setup(r => r.GetByUniqueIdAsync(uniqueId)).ReturnsAsync(new User
        {
            UniqueId = uniqueId,
            Roles = new List<User.Role> { User.Role.Resident },
            OwnedHomeIds = new List<Guid>()
        });

        var neverCompletes = new TaskCompletionSource<User>();
        users.Setup(r => r.UpsertAsync(It.IsAny<User>())).Returns(neverCompletes.Task);

        var homes = new Mock<IHomeRepository>();
        homes.Setup(r => r.GetByIdsAsync(It.IsAny<List<Guid>>())).ReturnsAsync(new List<Home>());

        var email = new Mock<IEmailService>();
        var controller = CreateController(users.Object, homes.Object, email.Object);

        var getTask = controller.Get();
        var completed = await Task.WhenAny(getTask, Task.Delay(250));

        Assert.Same(getTask, completed);
        Assert.NotNull(await getTask);
    }
}
