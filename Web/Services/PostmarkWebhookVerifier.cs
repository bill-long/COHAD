#nullable enable
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Web.Configuration;

namespace Web.Services
{
    public interface IPostmarkWebhookVerifier
    {
        /// <summary>
        /// Returns true if the webhook token is configured and verification is available.
        /// When false, the controller should reject requests in production (fail closed).
        /// </summary>
        bool IsConfigured { get; }

        /// <summary>
        /// Verifies the shared webhook token on a Postmark webhook request.
        /// </summary>
        bool Verify(string token);
    }

    /// <summary>
    /// Verifies incoming Postmark webhook requests by comparing the
    /// <c>X-Postmark-Webhook-Token</c> header against the configured shared secret.
    /// Uses timing-safe comparison to prevent timing attacks.
    /// </summary>
    public class PostmarkWebhookVerifier : IPostmarkWebhookVerifier
    {
        private readonly byte[]? _expectedTokenBytes;

        public PostmarkWebhookVerifier(IOptions<PostmarkOptions> options)
        {
            var token = options.Value.WebhookToken;
            if (!string.IsNullOrWhiteSpace(token))
            {
                _expectedTokenBytes = Encoding.UTF8.GetBytes(token);
            }
        }

        public bool IsConfigured => _expectedTokenBytes != null;

        public bool Verify(string token)
        {
            if (_expectedTokenBytes == null)
                return false;

            if (string.IsNullOrEmpty(token))
                return false;

            var incomingBytes = Encoding.UTF8.GetBytes(token);
            if (incomingBytes.Length != _expectedTokenBytes.Length)
                return false;
            return CryptographicOperations.FixedTimeEquals(incomingBytes, _expectedTokenBytes);
        }
    }
}
