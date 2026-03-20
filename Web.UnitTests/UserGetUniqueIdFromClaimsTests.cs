using System.Collections.Generic;
using System.Security.Claims;
using Web.Models;
using Xunit;

namespace Web.UnitTests;

public sealed class UserGetUniqueIdFromClaimsTests
{
    [Fact]
    public void Returns_concatenated_provider_and_name_identifier()
    {
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, "2fec9914-0562-4ff7-b827-8e5b39ce0978"),
            new Claim(
                "http://schemas.microsoft.com/identity/claims/identityprovider",
                "google.com")
        };
        var id = User.GetUniqueIdFromClaims(claims);
        Assert.Equal("google.com2fec9914-0562-4ff7-b827-8e5b39ce0978", id);
    }

    [Fact]
    public void Returns_null_when_name_identifier_missing()
    {
        var claims = new List<Claim>
        {
            new Claim(
                "http://schemas.microsoft.com/identity/claims/identityprovider",
                "google.com")
        };
        Assert.Throws<InvalidOperationException>(() => User.GetUniqueIdFromClaims(claims));
    }

    [Fact]
    public void Returns_null_when_identity_provider_missing()
    {
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, "abc")
        };
        Assert.Throws<InvalidOperationException>(() => User.GetUniqueIdFromClaims(claims));
    }
}
