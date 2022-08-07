using System;

namespace Web.Models
{
    public class Payment
    {
        public Guid Id { get; set; }

        public string PayerEmail { get; set; }

        public string PayerName { get; set; }

        public string Amount { get; set; }

        public DateTime? Date { get; set; }

        public Type PaymentType { get; set; }

        public string FullDetailsJSON { get; set; }

        public enum Type
        {
            OneTime,
            SubscriptionCreated
        }
    }
}
