#nullable enable
using System;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Web.Configuration;

namespace Web.Services
{
    public interface ISendGridWebhookVerifier
    {
        /// <summary>
        /// Returns true if the webhook signature is configured and verification is available.
        /// When false, the controller should accept all requests (development / not yet configured).
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

        public SendGridWebhookVerifier(IOptions<SendGridOptions> options)
        {
            var key = options.Value.WebhookVerificationKey;
            if (!string.IsNullOrWhiteSpace(key))
            {
                _ecdsa = ECDsa.Create();
                // SendGrid provides the verification key as a base64-encoded DER SubjectPublicKeyInfo
                _ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key), out _);
            }
        }

        public bool IsConfigured => _ecdsa != null;

        public bool Verify(string payload, string signature, string timestamp)
        {
            if (_ecdsa == null)
                return false;

            // SendGrid signs: timestamp + payload
            var dataToVerify = Encoding.UTF8.GetBytes(timestamp + payload);
            var signatureBytes = Convert.FromBase64String(signature);
            return _ecdsa.VerifyData(dataToVerify, signatureBytes, HashAlgorithmName.SHA256);
        }
    }
}
