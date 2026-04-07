using Web.Models;

namespace Web.Services
{
    /// <summary>
    /// Shared delivery-status severity logic used by both SendGrid and SES webhook controllers.
    /// </summary>
    public static class DeliveryStatusHelper
    {
        /// <summary>
        /// Determines whether the new delivery status should replace the existing one.
        /// Severity order: Unknown (0) &lt; Deferred (1) &lt; Delivered (2) &lt; Rejected (3) &lt; SpamReport (4) &lt; Bounced (5).
        /// </summary>
        public static bool ShouldUpdate(DeliveryStatus current, DeliveryStatus incoming)
        {
            return GetSeverity(incoming) > GetSeverity(current);
        }

        public static int GetSeverity(DeliveryStatus status) =>
            status switch
            {
                DeliveryStatus.Unknown => 0,
                DeliveryStatus.Deferred => 1,
                DeliveryStatus.Delivered => 2,
                DeliveryStatus.Rejected => 3,
                DeliveryStatus.SpamReport => 4,
                DeliveryStatus.Bounced => 5,
                _ => 0,
            };
    }
}
