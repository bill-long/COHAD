using System.Collections.Generic;

namespace Web.Configuration
{
    public class PostmarkOptions
    {
        /// <summary>
        /// Master switch for Postmark integration. When false, all emails go through the default
        /// transport (SendGrid) regardless of <see cref="RoutedRecipients"/>.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Postmark Server API Token. Used as the SMTP username when sending emails.
        /// Set via user secrets or environment variable (Postmark__ServerToken).
        /// </summary>
        public string ServerToken { get; set; } = "";

        /// <summary>
        /// Shared secret configured in the Postmark webhook settings.
        /// Compared against the <c>X-Postmark-Webhook-Token</c> header on incoming webhook requests.
        /// Set via user secrets or environment variable (Postmark__WebhookToken).
        /// </summary>
        public string WebhookToken { get; set; } = "";

        /// <summary>
        /// Postmark Message Stream ID for broadcast emails (committee blasts).
        /// </summary>
        public string BroadcastStream { get; set; } = "broadcast";

        /// <summary>
        /// Postmark Message Stream ID for transactional emails (notifications, forwards).
        /// </summary>
        public string TransactionalStream { get; set; } = "outbound";

        /// <summary>
        /// Email job categories that should be sent via the transactional stream.
        /// All other categories use the broadcast stream.
        /// </summary>
        public List<string> TransactionalCategories { get; set; } = new() { "registration", "committee-forward" };

        /// <summary>
        /// Recipient email addresses that should be routed through Postmark during
        /// the parallel testing phase. When empty and Postmark is enabled, no emails
        /// are routed to Postmark (opt-in per recipient).
        /// </summary>
        public List<string> RoutedRecipients { get; set; } = new();
    }
}
