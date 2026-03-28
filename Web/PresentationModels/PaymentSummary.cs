using System;
using Web.Models;

namespace Web.PresentationModels
{
    /// <summary>
    /// Resident-facing payment fields for API responses. Omits internal linkage and raw PayPal payloads.
    /// </summary>
    public sealed class PaymentSummary
    {
        public Guid Id { get; private set; }

        public string PayerEmail { get; private set; }

        public string Amount { get; private set; }

        public DateTime? Date { get; private set; }

        public Payment.Type PaymentType { get; private set; }

        public string PayPalTransactionId { get; private set; }

        public Guid? HomeId { get; private set; }

        public static PaymentSummary FromStorageModel(Payment payment)
        {
            if (payment == null)
            {
                throw new ArgumentNullException(nameof(payment));
            }

            return new PaymentSummary
            {
                Id = payment.Id,
                PayerEmail = payment.PayerEmail,
                Amount = payment.Amount,
                Date = payment.Date,
                PaymentType = payment.PaymentType,
                PayPalTransactionId = payment.PayPalTransactionId,
                HomeId = payment.HomeId
            };
        }
    }
}
