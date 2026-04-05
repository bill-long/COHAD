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

        private const int NonceSize = 12; // AES-GCM standard nonce
        private const int TagSize = 16; // AES-GCM auth tag

        private readonly byte[] _encryptionKey;

        public UnsubscribeTokenService(IOptions<UnsubscribeTokenOptions> options)
        {
            var key = options.Value.SigningKey;
            if (string.IsNullOrWhiteSpace(key) || Encoding.UTF8.GetByteCount(key) < 32)
                throw new InvalidOperationException("UnsubscribeToken:SigningKey must be at least 32 UTF-8 bytes.");

            // Derive a fixed 256-bit encryption key from the configurable signing key
            _encryptionKey = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        }

        public string GenerateToken(Guid homeId, string email)
        {
            return GenerateToken(homeId, email, DateTimeOffset.UtcNow);
        }

        internal string GenerateToken(Guid homeId, string email, DateTimeOffset issued)
        {
            if (string.IsNullOrWhiteSpace(email))
                throw new ArgumentException("Email must not be empty.", nameof(email));

            var unixSeconds = issued.ToUnixTimeSeconds();
            var payload = $"{homeId:D}|{email}|{unixSeconds}";
            var plaintext = Encoding.UTF8.GetBytes(payload);

            var nonce = new byte[NonceSize];
            RandomNumberGenerator.Fill(nonce);

            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagSize];

            using var aes = new AesGcm(_encryptionKey, TagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag);

            // Token = base64url(nonce + ciphertext + tag)
            var combined = new byte[NonceSize + ciphertext.Length + TagSize];
            nonce.CopyTo(combined, 0);
            ciphertext.CopyTo(combined, NonceSize);
            tag.CopyTo(combined, NonceSize + ciphertext.Length);

            return Base64UrlEncode(combined);
        }

        public UnsubscribeTokenPayload ValidateToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return null;

            byte[] combined;
            try
            {
                combined = Base64UrlDecode(token.AsSpan());
            }
            catch
            {
                return null;
            }

            if (combined.Length < NonceSize + TagSize + 1)
                return null;

            var nonce = combined.AsSpan(0, NonceSize);
            var ciphertextLength = combined.Length - NonceSize - TagSize;
            var ciphertext = combined.AsSpan(NonceSize, ciphertextLength);
            var tag = combined.AsSpan(NonceSize + ciphertextLength, TagSize);

            var plaintext = new byte[ciphertextLength];
            try
            {
                using var aes = new AesGcm(_encryptionKey, TagSize);
                aes.Decrypt(nonce, ciphertext, tag, plaintext);
            }
            catch (CryptographicException)
            {
                return null;
            }

            var payload = Encoding.UTF8.GetString(plaintext);

            // Parse as {guid}|{email}|{unixSeconds}. Use first/last '|' positions
            // so that '|' characters within the email local-part don't break parsing.
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
                Issued = issued,
            };
        }

        private static string Base64UrlEncode(byte[] data)
        {
            return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static byte[] Base64UrlDecode(ReadOnlySpan<char> base64Url)
        {
            var s = base64Url.ToString().Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2:
                    s += "==";
                    break;
                case 3:
                    s += "=";
                    break;
            }
            return Convert.FromBase64String(s);
        }
    }
}
