namespace Web.Configuration
{
    public class SendGridOptions
    {
        /// <summary>
        /// ECDSA public key for verifying SendGrid Event Webhook signatures.
        /// Set via user secrets or environment variable (SendGrid__WebhookVerificationKey).
        /// </summary>
        public string WebhookVerificationKey { get; set; } = string.Empty;
    }
}
