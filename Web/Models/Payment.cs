using System;

namespace Web.Models
{
    public class Payment
    {
        public Guid Id { get; set; }

        public string PayerUniqueId { get; set; }

        public string PayerEmail { get; set; }

        public string PayerName { get; set; }

        public string Amount { get; set; }

        public DateTime? Date { get; set; }

        public Type PaymentType { get; set; }

        public string FullDetailsJSON { get; set; }

        /// <summary>
        /// When set (e.g. PayPal sync), dues for any owner of this home may see the payment if the payer email still appears on the home.
        /// </summary>
        public Guid? HomeId { get; set; }

        /// <summary>
        /// PayPal <c>transaction_info.transaction_id</c> from Transaction Search, when known.
        /// Used to dedupe synced rows and client-recorded payments.
        /// </summary>
        public string PayPalTransactionId { get; set; }

        public enum Type
        {
            OneTime,
            SubscriptionCreated,
            /// <summary>Inserted by scheduled PayPal Transaction Search sync.</summary>
            PayPalSync
        }
    }
}
