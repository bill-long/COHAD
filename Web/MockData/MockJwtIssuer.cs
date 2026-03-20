using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Web.MockData
{
    public static class MockJwtIssuer
    {
        public const string Issuer = "https://cohad.mock/";
        public const string Audience = "cohad-mock-api";

        public static string CreateAccessToken(string signingKey, TimeSpan lifetime)
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
            var claims = new List<Claim>
            {
                new Claim(JwtRegisteredClaimNames.Sub, MockDataConstants.NameIdentifier),
                new Claim(System.Security.Claims.ClaimTypes.NameIdentifier, MockDataConstants.NameIdentifier),
                new Claim("http://schemas.microsoft.com/identity/claims/identityprovider", MockDataConstants.IdentityProvider),
                new Claim("http://schemas.microsoft.com/identity/claims/scope", "API"),
                new Claim(System.Security.Claims.ClaimTypes.GivenName, "Mock"),
                new Claim(System.Security.Claims.ClaimTypes.Surname, "Resident"),
                new Claim("emails", "mock@cohad.local"),
                new Claim("streetAddress", "123 Mock Lane"),
            };

            var token = new JwtSecurityToken(
                issuer: Issuer,
                audience: Audience,
                claims: claims,
                expires: DateTime.UtcNow.Add(lifetime),
                signingCredentials: creds);

            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }
}
