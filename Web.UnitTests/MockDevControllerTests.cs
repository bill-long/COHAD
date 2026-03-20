using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Moq;
using Web.Controllers;

namespace Web.UnitTests;

public sealed class MockDevControllerTests
{
    private const string ValidSigningKey = "01234567890123456789012345678901";

    [Fact]
    public void GetMockToken_returns_not_found_for_non_loopback_request()
    {
        var controller = CreateController(IPAddress.Parse("203.0.113.10"));

        var result = controller.GetMockToken(CreateEnvironment("MockData"), CreateConfiguration(ValidSigningKey));

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public void GetMockToken_returns_ok_for_loopback_with_short_lifetime()
    {
        var controller = CreateController(IPAddress.Loopback);

        var nowUtc = DateTime.UtcNow;
        var result = controller.GetMockToken(CreateEnvironment("MockData"), CreateConfiguration(ValidSigningKey));

        var ok = Assert.IsType<OkObjectResult>(result);
        var accessToken = GetProperty<string>(ok.Value!, "accessToken");
        var expiresIn = GetProperty<int>(ok.Value!, "expiresIn");

        Assert.Equal(900, expiresIn);

        var token = new JwtSecurityTokenHandler().ReadJwtToken(accessToken);
        var remaining = token.ValidTo - nowUtc;
        Assert.InRange(remaining, TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(16));
    }

    [Fact]
    public void GetMockToken_returns_not_found_when_not_in_mock_data_environment()
    {
        var controller = CreateController(IPAddress.Loopback);

        var result = controller.GetMockToken(CreateEnvironment("Development"), CreateConfiguration(ValidSigningKey));

        Assert.IsType<NotFoundResult>(result);
    }

    private static MockDevController CreateController(IPAddress remoteAddress)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = remoteAddress;

        return new MockDevController
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = httpContext
            }
        };
    }

    private static IWebHostEnvironment CreateEnvironment(string environmentName)
    {
        var env = new Mock<IWebHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns(environmentName);
        return env.Object;
    }

    private static IConfiguration CreateConfiguration(string signingKey)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MockJwt:SigningKey"] = signingKey
            })
            .Build();
    }

    private static T GetProperty<T>(object source, string propertyName)
    {
        var property = source.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        var value = property.GetValue(source);
        Assert.NotNull(value);
        return (T)value!;
    }
}
