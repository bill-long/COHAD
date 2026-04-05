using System;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace Web.MockData
{
    /// <summary>
    /// Resolves the HS256 signing key for MockData JWTs from configuration (including
    /// <c>MockJwt__SigningKey</c> environment variable) or user secrets — never commit real keys in appsettings.
    /// </summary>
    public static class MockJwtSigningKey
    {
        /// <summary>Minimum UTF-8 byte length for HS256 key material (256 bits).</summary>
        public const int MinUtf8ByteLength = 32;

        public static string Resolve(IConfiguration configuration)
        {
            var key = configuration["MockJwt:SigningKey"];
            return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        }

        /// <summary>
        /// Returns a validated signing key or throws <see cref="InvalidOperationException"/> with actionable guidance.
        /// </summary>
        public static string ResolveValidated(IConfiguration configuration)
        {
            var key = Resolve(configuration);
            EnsureValid(key);
            return key;
        }

        public static void EnsureValid(string signingKey)
        {
            if (string.IsNullOrEmpty(signingKey))
            {
                throw new InvalidOperationException(
                    "MockData requires a non-empty MockJwt:SigningKey. Set user secret "
                        + "(dotnet user-secrets set \"MockJwt:SigningKey\" \"<32+ UTF-8 bytes>\") or environment variable "
                        + "MockJwt__SigningKey (do not commit real keys)."
                );
            }

            var byteCount = Encoding.UTF8.GetByteCount(signingKey);
            if (byteCount < MinUtf8ByteLength)
            {
                throw new InvalidOperationException(
                    $"MockJwt:SigningKey must be at least {MinUtf8ByteLength} UTF-8 bytes for HS256 (got {byteCount})."
                );
            }
        }
    }
}
