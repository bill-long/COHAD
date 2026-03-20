using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Web.Models;
using Web.Services.Repositories;

namespace Web.MockData
{
    public sealed class MockPaymentRepository : IPaymentRepository
    {
        private readonly List<Payment> _payments = new();
        private readonly object _lock = new();

        public Task<List<Payment>> GetByPayerUniqueIdAsync(string uniqueId)
        {
            lock (_lock)
            {
                var list = _payments
                    .Where(p => p.PayerUniqueId == uniqueId)
                    .Select(ClonePayment)
                    .ToList();
                return Task.FromResult(list);
            }
        }

        public Task<Payment> AddAsync(Payment payment)
        {
            lock (_lock)
            {
                var copy = ClonePayment(payment);
                if (copy.Id == Guid.Empty)
                {
                    copy.Id = Guid.NewGuid();
                }

                _payments.Add(copy);
                return Task.FromResult(ClonePayment(copy));
            }
        }

        private static Payment ClonePayment(Payment p)
        {
            if (p == null)
            {
                return null;
            }

            return new Payment
            {
                Id = p.Id,
                PayerUniqueId = p.PayerUniqueId,
                PayerEmail = p.PayerEmail,
                PayerName = p.PayerName,
                Amount = p.Amount,
                Date = p.Date,
                PaymentType = p.PaymentType,
                FullDetailsJSON = p.FullDetailsJSON
            };
        }
    }
}
