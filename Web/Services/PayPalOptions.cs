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

        /// <summary>When true, the weekly Azure Function runs Transaction Search sync.</summary>
        public bool SyncEnabled { get; set; }

        /// <summary>How far back to pull transactions (rolling), in UTC.</summary>
        public int SyncLookbackDays { get; set; } = 90;
    }
}
