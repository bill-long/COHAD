using System.Collections.Generic;
using System.Threading.Tasks;
using Web.Models;
using Web.Services.Repositories;

namespace Web.MockData
{
    public sealed class MockPaymentRepository : IPaymentRepository
    {
        public Task<List<Payment>> GetByPayerUniqueIdAsync(string uniqueId)
        {
            return Task.FromResult(new List<Payment>());
        }

        public Task<Payment> AddAsync(Payment payment)
        {
            return Task.FromResult(payment);
        }
    }
}
