namespace Web.Services
{
    /// <summary>
    /// PayPal REST credentials and sync settings (bind section <c>PayPal</c>).
    /// </summary>
    public sealed class PayPalOptions
    {
        public string ClientId { get; set; }

        public string ClientSecret { get; set; }

        /// <summary>
        /// Default <c>https://api-m.paypal.com</c>; use <c>https://api-m.sandbox.paypal.com</c> for sandbox.
        /// </summary>
        public string ApiBaseUrl { get; set; } = "https://api-m.paypal.com";

        /// <summary>When true, <see cref="PayPalSyncService"/> runs Transaction Search sync on its interval.</summary>
        public bool SyncEnabled { get; set; }

        /// <summary>How far back to pull transactions (rolling), in UTC.</summary>
        public int SyncLookbackDays { get; set; } = 90;

        /// <summary>
        /// Minimum days between successful syncs. Paced from the persisted last-success timestamp rather
        /// than an in-process timer, so the cadence survives restarts and deployments.
        /// </summary>
        public int SyncIntervalDays { get; set; } = 7;

        /// <summary>
        /// Minimum hours between attempts after a failed sync. Bounds retry volume when PayPal is
        /// unreachable or credentials are wrong, without waiting out a full <see cref="SyncIntervalDays"/>.
        /// </summary>
        public int SyncRetryIntervalHours { get; set; } = 6;

        /// <summary>How often <see cref="PayPalSyncService"/> wakes to check whether a sync is due.</summary>
        public int SyncCheckIntervalMinutes { get; set; } = 60;

        /// <summary>
        /// Delay before the first due-check after startup, so the app finishes booting before a sync can
        /// start. Consumed by <see cref="PayPalSyncService"/>; tests set it to zero.
        /// </summary>
        public int StartupDelaySeconds { get; set; } = 15;
    }
}
