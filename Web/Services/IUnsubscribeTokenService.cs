using System;

namespace Web.Services
{
    public interface IUnsubscribeTokenService
    {
        /// <summary>
        /// Generates an opaque, AES-GCM-encrypted token for the given home and email.
        /// The token is a base64url string containing nonce + ciphertext + authentication tag.
        /// </summary>
        string GenerateToken(Guid homeId, string email);

        /// <summary>
        /// Decrypts and validates a token, extracting the home ID and email address.
        /// Returns null if the token is invalid, tampered with, or expired.
        /// </summary>
        UnsubscribeTokenPayload ValidateToken(string token);
    }

    public class UnsubscribeTokenPayload
    {
        public Guid HomeId { get; set; }
        public string Email { get; set; }
        public DateTimeOffset Issued { get; set; }
    }

    /// <summary>
    /// No-op implementation used when no signing key is configured.
    /// GenerateToken returns null; ValidateToken always returns null.
    /// </summary>
    public class NullUnsubscribeTokenService : IUnsubscribeTokenService
    {
        public string GenerateToken(Guid homeId, string email) => null;

        public UnsubscribeTokenPayload ValidateToken(string token) => null;
    }
}
