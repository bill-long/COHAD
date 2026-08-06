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

        /// <summary>Inclusive bounds of the range <see cref="DateTimeOffset.FromUnixTimeSeconds"/> accepts.</summary>
        private static readonly long MinUnixSeconds = DateTimeOffset.MinValue.ToUnixTimeSeconds();
        private static readonly long MaxUnixSeconds = DateTimeOffset.MaxValue.ToUnixTimeSeconds();

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
            return Encrypt($"{homeId:D}|{email}|{unixSeconds}");
        }

        /// <summary>
        /// Encrypts an arbitrary payload string.
        /// <para>
        /// Internal rather than private so tests can mint a token that authenticates but carries a
        /// payload <see cref="GenerateToken(Guid, string)"/> would never emit. That is the only way
        /// to reach <see cref="ValidateToken"/>'s post-decryption parsing paths, which are exactly
        /// the ones the design doc's untrusted legacy key makes reachable in production.
        /// </para>
        /// </summary>
        internal string Encrypt(string payload)
        {
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

        public UnsubscribeTokenResult ValidateToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return UnsubscribeTokenResult.Failed(UnsubscribeTokenFailure.Missing);

            byte[] combined;
            try
            {
                combined = Base64UrlDecode(token.AsSpan());
            }
            catch (FormatException)
            {
                return UnsubscribeTokenResult.Failed(UnsubscribeTokenFailure.MalformedBase64);
            }

            if (combined.Length < NonceSize + TagSize + 1)
                return UnsubscribeTokenResult.Failed(UnsubscribeTokenFailure.TooShort);

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
                return UnsubscribeTokenResult.Failed(UnsubscribeTokenFailure.DecryptFailed);
            }

            var payload = Encoding.UTF8.GetString(plaintext);

            // Parse as {guid}|{email}|{unixSeconds}. Use first/last '|' positions
            // so that '|' characters within the email local-part don't break parsing.
            var firstPipe = payload.IndexOf('|');
            var lastPipe = payload.LastIndexOf('|');
            if (firstPipe < 0 || lastPipe <= firstPipe)
                return UnsubscribeTokenResult.Failed(UnsubscribeTokenFailure.MalformedPayload);

            var guidPart = payload[..firstPipe];
            var emailPart = payload[(firstPipe + 1)..lastPipe];
            var timestampPart = payload[(lastPipe + 1)..];

            if (!Guid.TryParse(guidPart, out var homeId))
                return UnsubscribeTokenResult.Failed(UnsubscribeTokenFailure.MalformedPayload);

            // GenerateToken refuses an empty email, but validation has to refuse one too: a payload
            // of {guid}||{unix} parses cleanly and would authorise an empty address, which
            // FindMatchingEmailAddresses normalises to "" and then matches against every
            // blank-address record on the home. Anyone able to mint a payload - the legacy key is
            // treated as untrusted, see docs/email-suppression-and-unsubscribe.md - could read and
            // clear those records' preferences.
            if (string.IsNullOrWhiteSpace(emailPart))
                return UnsubscribeTokenResult.Failed(UnsubscribeTokenFailure.MalformedPayload);

            if (!long.TryParse(timestampPart, out var unixSeconds))
                return UnsubscribeTokenResult.Failed(UnsubscribeTokenFailure.MalformedPayload);

            // long.TryParse accepts values far outside what FromUnixTimeSeconds supports, and that
            // call would throw rather than return. Every rejection has to name a reason - an
            // exception here would surface as a 500 with nothing logged, which is precisely the
            // blind spot this type was reworked to remove.
            if (unixSeconds < MinUnixSeconds || unixSeconds > MaxUnixSeconds)
                return UnsubscribeTokenResult.Failed(UnsubscribeTokenFailure.MalformedPayload);

            var issued = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            var age = DateTimeOffset.UtcNow - issued;

            // Order matters for diagnosis, not for the outcome: a future timestamp and an expired
            // one are both rejections, but they point at different causes (clock skew vs an old link).
            if (age < TimeSpan.FromMinutes(-5))
                return UnsubscribeTokenResult.Failed(UnsubscribeTokenFailure.IssuedInFuture);

            if (age > MaxTokenAge)
                return UnsubscribeTokenResult.Failed(UnsubscribeTokenFailure.Expired);

            return UnsubscribeTokenResult.Success(
                new UnsubscribeTokenPayload
                {
                    HomeId = homeId,
                    Email = emailPart,
                    Issued = issued,
                }
            );
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
