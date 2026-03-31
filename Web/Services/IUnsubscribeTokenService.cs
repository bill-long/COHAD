using System;

namespace Web.Services
{
    public interface IUnsubscribeTokenService
    {
        /// <summary>
        /// Generates an HMAC-signed token encoding the home ID and email address.
        /// </summary>
        string GenerateToken(Guid homeId, string email);

        /// <summary>
        /// Validates a token and extracts the home ID and email address.
        /// Returns null if the token is invalid or tampered with.
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
