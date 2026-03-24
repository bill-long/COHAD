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

        public static string CreateAccessToken(string signingKey, TimeSpan lifetime, string userId = null)
        {
            string nameIdentifier;
            string givenName, surname, email, streetAddress;

            if (userId == MockDataConstants.SecondaryUserNameIdentifier)
            {
                nameIdentifier = MockDataConstants.SecondaryUserNameIdentifier;
                givenName = "Taylor";
                surname = "Neighbor";
                email = "taylor@cohad.local";
                streetAddress = "456 Test Court";
            }
            else
            {
                nameIdentifier = MockDataConstants.AdminNameIdentifier;
                givenName = "Mock";
                surname = "Resident";
                email = "mock@cohad.local";
                streetAddress = "123 Mock Lane";
            }

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
            var claims = new List<Claim>
            {
                new Claim(JwtRegisteredClaimNames.Sub, nameIdentifier),
                new Claim(System.Security.Claims.ClaimTypes.NameIdentifier, nameIdentifier),
                new Claim("http://schemas.microsoft.com/identity/claims/identityprovider", MockDataConstants.IdentityProvider),
                new Claim("http://schemas.microsoft.com/identity/claims/scope", "API"),
                new Claim(System.Security.Claims.ClaimTypes.GivenName, givenName),
                new Claim(System.Security.Claims.ClaimTypes.Surname, surname),
                new Claim("emails", email),
                new Claim("streetAddress", streetAddress),
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
