namespace Web.Configuration
{
    public class SendGridOptions
    {
        /// <summary>
        /// Base64-encoded DER SubjectPublicKeyInfo for verifying SendGrid Event Webhook
        /// ECDSA signatures. Obtain from the SendGrid Event Webhook settings page.
        /// Set via user secrets or environment variable (SendGrid__WebhookVerificationKey).
        /// </summary>
        public string WebhookVerificationKey { get; set; } = string.Empty;
    }
}
