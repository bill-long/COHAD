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

            // Parse as {guid}|{email}|{unixSeconds}. Use first/last '|' positions
            // so that '|' characters within the email local-part don't break parsing
            // (GUIDs and unix timestamps never contain '|').
            var firstPipe = payload.IndexOf('|');
            var lastPipe = payload.LastIndexOf('|');
            if (firstPipe < 0 || lastPipe <= firstPipe)
                return null;

            var guidPart = payload[..firstPipe];
            var emailPart = payload[(firstPipe + 1)..lastPipe];
            var timestampPart = payload[(lastPipe + 1)..];

            if (!Guid.TryParse(guidPart, out var homeId))
                return null;

            if (!long.TryParse(timestampPart, out var unixSeconds))
                return null;

            var issued = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            var age = DateTimeOffset.UtcNow - issued;
            if (age < TimeSpan.FromMinutes(-5) || age > MaxTokenAge)
                return null;

            return new UnsubscribeTokenPayload
            {
                HomeId = homeId,
                Email = emailPart,
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
