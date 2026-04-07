namespace Web.Configuration
{
    public class SmtpOptions
    {
        public string SmtpHost { get; set; }

        public string SmtpUser { get; set; }

        public string SmtpPassword { get; set; }

        /// <summary>
        /// Per-operation timeout in seconds for SMTP connect, authenticate, and send.
        /// MailKit defaults to 120s which is too long for a relay send through SendGrid.
        /// </summary>
        public int TimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// Maximum idle time in seconds before forcing a reconnect.
        /// SendGrid load balancers can silently drop idle TCP connections;
        /// MailKit's IsConnected only checks local state and won't detect this.
        /// </summary>
        public int MaxIdleSeconds { get; set; } = 60;
    }
}
