using System;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Web.Configuration;

namespace Web.Services
{
    public class UnsubscribeTokenService : IUnsubscribeTokenService
    {
        /// <summary>Maximum token age. Tokens older than this are rejected.</summary>
        internal static readonly TimeSpan MaxTokenAge = TimeSpan.FromDays(365);

        private readonly byte[] _keyBytes;

        public UnsubscribeTokenService(IOptions<UnsubscribeTokenOptions> options)
        {
            var key = options.Value.SigningKey;
            if (string.IsNullOrEmpty(key) || Encoding.UTF8.GetByteCount(key) < 32)
                throw new InvalidOperationException(
                    "UnsubscribeToken:SigningKey must be at least 32 UTF-8 bytes.");

            _keyBytes = Encoding.UTF8.GetBytes(key);
        }

        public string GenerateToken(Guid homeId, string email)
        {
            if (string.IsNullOrWhiteSpace(email))
                throw new ArgumentException("Email must not be empty.", nameof(email));

            var unixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var payload = $"{homeId:D}|{email}|{unixSeconds}";
            var payloadBytes = Encoding.UTF8.GetBytes(payload);

            var signature = ComputeHmac(payloadBytes);

            return $"{Base64UrlEncode(payloadBytes)}.{Base64UrlEncode(signature)}";
        }

        public UnsubscribeTokenPayload ValidateToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return null;

            var dotIndex = token.IndexOf('.');
            if (dotIndex < 1 || dotIndex >= token.Length - 1)
                return null;

            byte[] payloadBytes, signatureBytes;
            try
            {
                payloadBytes = Base64UrlDecode(token.AsSpan(0, dotIndex));
                signatureBytes = Base64UrlDecode(token.AsSpan(dotIndex + 1));
            }
            catch
            {
                return null;
            }

            var expectedSignature = ComputeHmac(payloadBytes);
            if (!CryptographicOperations.FixedTimeEquals(signatureBytes, expectedSignature))
                return null;

            var payload = Encoding.UTF8.GetString(payloadBytes);
            var parts = payload.Split('|');
            if (parts.Length != 3)
                return null;

            if (!Guid.TryParse(parts[0], out var homeId))
                return null;

            if (!long.TryParse(parts[2], out var unixSeconds))
                return null;

            var issued = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            if (DateTimeOffset.UtcNow - issued > MaxTokenAge)
                return null;

            return new UnsubscribeTokenPayload
            {
                HomeId = homeId,
                Email = parts[1],
                Issued = issued
            };
        }

        private byte[] ComputeHmac(byte[] data)
        {
            using var hmac = new HMACSHA256(_keyBytes);
            return hmac.ComputeHash(data);
        }

        private static string Base64UrlEncode(byte[] data)
        {
            return Convert.ToBase64String(data)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        private static byte[] Base64UrlDecode(ReadOnlySpan<char> base64Url)
        {
            var s = base64Url.ToString().Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
            }
            return Convert.FromBase64String(s);
        }
    }
}
