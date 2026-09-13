namespace Web.PresentationModels
{
    /// <summary>
    /// Deployment-configured payment details the Dues page renders. Returned by <c>GET api/payment/options</c>.
    /// </summary>
    public sealed class PaymentOptions
    {
        /// <summary>
        /// The Zelle recipient email address, or <c>null</c> when none is configured. The page shows the
        /// Zelle instructions only when this is set.
        /// </summary>
        public string ZelleEmail { get; set; }
    }
}
