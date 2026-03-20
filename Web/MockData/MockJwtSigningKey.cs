using Microsoft.Extensions.Configuration;

namespace Web.MockData
{
    /// <summary>
    /// Resolves the HS256 signing key for MockData JWTs from configuration (including
    /// <c>MockJwt__SigningKey</c> environment variable) or user secrets — never commit real keys in appsettings.
    /// </summary>
    public static class MockJwtSigningKey
    {
        public static string Resolve(IConfiguration configuration)
        {
            var key = configuration["MockJwt:SigningKey"];
            return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        }
    }
}
