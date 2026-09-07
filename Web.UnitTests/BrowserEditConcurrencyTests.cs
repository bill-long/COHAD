using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Web.Controllers;
using Web.MockData;
using Web.Models;
using Web.Services;
using Web.Services.Repositories;

namespace Web.UnitTests;

public sealed class BrowserEditConcurrencyTests
{
    private readonly MockUserRepository users = new();
    private readonly MockHomeRepository homes = new();
    private readonly Mock<IAuditLogRepository> audit = new();
    private readonly Mock<IResidentRepository> residents = new();
    private readonly Mock<IEventSignupConversionService> conversion = new();

    private async Task<IHost> StartAsync()
    {
        residents.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Resident>());
        residents.Setup(r => r.GetByHomeIdAsync(It.IsAny<Guid>())).ReturnsAsync(new List<Resident>());
        return await new HostBuilder().ConfigureWebHost(web => web.UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddAuthorization(options =>
                {
                    options.AddPolicy("Administrator", p => p.RequireRole("Administrator"));
                    options.AddPolicy("Resident", p => p.RequireRole("Resident"));
                });
                services.AddControllers(o => o.Filters.Add<ConcurrencyConflictExceptionFilter>())
                    .AddApplicationPart(typeof(UserController).Assembly).AddControllersAsServices();
                services.AddTransient(_ => new UserController(users, new CurrentUserAccessor(users), homes,
                    residents.Object, audit.Object, conversion.Object, NullLogger<UserController>.Instance));
                services.AddTransient(_ => new HomeController(users, new CurrentUserAccessor(users), homes,
                    residents.Object, audit.Object, null, NullLogger<HomeController>.Instance));
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.Use(async (context, next) =>
                {
                    context.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.NameIdentifier, MockDataConstants.AdminNameIdentifier),
                        new Claim("http://schemas.microsoft.com/identity/claims/identityprovider", MockDataConstants.IdentityProvider),
                        new Claim(ClaimTypes.Role, "Administrator"), new Claim(ClaimTypes.Role, "Resident"),
                    }, "Test"));
                    await next();
                });
                app.UseAuthorization();
                app.UseEndpoints(e => e.MapControllers());
            })).StartAsync();
    }

    private static string Path(string kind) => kind switch
    {
        "profile" => "/api/user",
        "associations" => $"/api/user/{MockDataConstants.SecondaryUserUniqueId}/associations",
        _ => "/api/home",
    };

    private static object Payload(string kind, string? tag) => kind switch
    {
        "profile" => new { uniqueId = MockDataConstants.SecondaryUserUniqueId, givenName = "Changed", eTag = tag },
        "associations" => new { roleNames = new[] { "Resident" }, ownedHomeIds = new[] { MockDataConstants.SecondSampleHomeId }, eTag = tag },
        _ => new { id = MockDataConstants.SecondSampleHomeId, emailAddress = new { address = "changed@cohad.local" }, eTag = tag },
    };

    [Theory]
    [InlineData("profile")]
    [InlineData("associations")]
    [InlineData("home")]
    public async Task Browser_snapshot_conflicts_and_fresh_save_succeeds(string kind)
    {
        using var host = await StartAsync();
        using var client = host.GetTestClient();
        var listPath = kind == "home" ? "/api/home" : "/api/user";
        var snapshot = await client.GetFromJsonAsync<System.Text.Json.Nodes.JsonArray>(listPath);
        var row = snapshot!.Single(n => n![kind == "home" ? "id" : "uniqueId"]!.GetValue<string>() ==
            (kind == "home" ? MockDataConstants.SecondSampleHomeId.ToString() : MockDataConstants.SecondaryUserUniqueId));
        var tag = row!["eTag"]!.GetValue<string>();
        var winner = await client.PutAsJsonAsync(Path(kind), Payload(kind, tag));
        Assert.Equal(HttpStatusCode.OK, winner.StatusCode);
        audit.Invocations.Clear();
        conversion.Invocations.Clear();
        residents.Invocations.Clear();

        // The controller will re-read a newer version. It must still enforce the old browser version.
        var loser = await client.PutAsJsonAsync(Path(kind), Payload(kind, tag));
        Assert.Equal(HttpStatusCode.Conflict, loser.StatusCode);
        Assert.Contains("refresh", await loser.Content.ReadAsStringAsync());
        Assert.Empty(audit.Invocations);
        Assert.Empty(conversion.Invocations);
        Assert.DoesNotContain(residents.Invocations, i => i.Method.Name is "UpsertAsync" or "DeleteAsync");

        var currentTag = kind == "home"
            ? (await homes.GetByIdAsync(MockDataConstants.SecondSampleHomeId)).ETag
            : (await users.GetByUniqueIdAsync(MockDataConstants.SecondaryUserUniqueId)).ETag;
        Assert.NotEqual(tag, currentTag);
        if (kind != "home")
        {
            var saved = await winner.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>();
            Assert.Equal(currentTag, saved!["eTag"]!.GetValue<string>());
        }
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(Path(kind), Payload(kind, currentTag))).StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("*")]
    [InlineData(" * ")]
    public async Task Invalid_version_is_rejected_by_MVC_for_every_update(string? tag)
    {
        using var host = await StartAsync();
        using var client = host.GetTestClient();
        foreach (var kind in new[] { "profile", "associations", "home" })
        {
            var response = await client.PutAsJsonAsync(Path(kind), Payload(kind, tag));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("Refresh to load", await response.Content.ReadAsStringAsync());
        }
        Assert.Empty(audit.Invocations);
        Assert.Empty(conversion.Invocations);
        Assert.Empty(residents.Invocations);
    }

    [Fact]
    public async Task Stale_home_with_resident_changes_has_no_resident_side_effects()
    {
        using var host = await StartAsync();
        using var client = host.GetTestClient();
        var home = await homes.GetByIdAsync(MockDataConstants.SecondSampleHomeId);
        var tag = home.ETag;
        await homes.UpsertAsync(home);
        var response = await client.PutAsJsonAsync("/api/home", new
        {
            id = home.Id, eTag = tag,
            residents = new[] { new { givenName = "New resident", residentType = 0 } },
        });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        residents.Verify(r => r.UpsertAsync(It.IsAny<Resident>()), Times.Never);
        residents.Verify(r => r.DeleteAsync(It.IsAny<Guid>()), Times.Never);
        Assert.Empty(audit.Invocations);
    }
}
