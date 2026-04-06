#nullable enable
using System;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Web.Configuration;

namespace Web.Services
{
    public interface ISendGridWebhookVerifier
    {
        /// <summary>
        /// Returns true if the webhook signature is configured and verification is available.
        /// When false, the controller should reject requests in production (fail closed).
        /// </summary>
        bool IsConfigured { get; }

        /// <summary>
        /// Verifies the ECDSA signature on a SendGrid Event Webhook request.
        /// </summary>
        bool Verify(string payload, string signature, string timestamp);
    }

    public class SendGridWebhookVerifier : ISendGridWebhookVerifier
    {
        private readonly ECDsa? _ecdsa;
        private readonly ILogger<SendGridWebhookVerifier> _logger;

        public SendGridWebhookVerifier(IOptions<SendGridOptions> options, ILogger<SendGridWebhookVerifier> logger)
        {
            _logger = logger;
            var key = options.Value.WebhookVerificationKey;
            if (!string.IsNullOrWhiteSpace(key))
            {
                try
                {
                    _ecdsa = ECDsa.Create();
                    // SendGrid provides the verification key as a base64-encoded DER SubjectPublicKeyInfo
                    _ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key), out _);
                }
                catch (Exception ex) when (ex is FormatException or CryptographicException)
                {
                    _logger.LogError(ex, "SendGrid webhook verification key is malformed — verification disabled.");
                    _ecdsa?.Dispose();
                    _ecdsa = null;
                }
            }
        }

        public bool IsConfigured => _ecdsa != null;

        public bool Verify(string payload, string signature, string timestamp)
        {
            if (_ecdsa == null)
                return false;

            try
            {
                // SendGrid signs: timestamp + payload
                var dataToVerify = Encoding.UTF8.GetBytes(timestamp + payload);
                var signatureBytes = Convert.FromBase64String(signature);
                return _ecdsa.VerifyData(dataToVerify, signatureBytes, HashAlgorithmName.SHA256);
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException)
            {
                _logger.LogWarning(ex, "SendGrid webhook signature verification threw — treating as invalid.");
                return false;
            }
        }
    }
}
