using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Web.Models;
using Web.Services;
using Web.Services.Repositories;

namespace Web.MockData
{
    public sealed class MockPaymentRepository : IPaymentRepository
    {
        private readonly IHomeRepository _homes;
        private readonly IResidentRepository _residents;
        private readonly IUserRepository _users;
        private readonly List<Payment> _payments = new();
        private readonly object _lock = new();

        public MockPaymentRepository(IHomeRepository homes, IResidentRepository residents, IUserRepository users)
        {
            _homes = homes;
            _residents = residents;
            _users = users;
        }

        public Task<List<Payment>> GetByPayerUniqueIdAsync(string uniqueId)
        {
            lock (_lock)
            {
                var list = _payments.Where(p => p.PayerUniqueId == uniqueId).Select(ClonePayment).ToList();
                return Task.FromResult(list);
            }
        }

        public async Task<List<Payment>> GetPaymentsForResidentAsync(User user)
        {
            if (user == null)
            {
                return new List<Payment>();
            }

            List<Payment> snapshot;
            lock (_lock)
            {
                snapshot = _payments.Select(ClonePayment).ToList();
            }

            var byId = new Dictionary<Guid, Payment>();

            foreach (var p in snapshot.Where(x => x.PayerUniqueId == user.UniqueId))
            {
                byId[p.Id] = p;
            }

            foreach (var p in snapshot.Where(x => !x.HomeId.HasValue && string.IsNullOrEmpty(x.PayerUniqueId)))
            {
                if (UserEmailHelpers.EmailMatchesUser(p.PayerEmail, user))
                {
                    byId[p.Id] = p;
                }
            }

            var owned = user.OwnedHomeIds ?? new List<Guid>();
            if (owned.Count > 0)
            {
                var homes = await _homes.GetByIdsAsync(owned).ConfigureAwait(false);
                var allResidents = await _residents.GetAllAsync().ConfigureAwait(false);
                var residentsByHome = allResidents.GroupBy(r => r.HomeId).ToDictionary(g => g.Key, g => g.ToList());
                var emailSets = PaymentHomeAssociation.BuildEmailSetsByHome(homes, residentsByHome);
                var allUsers = await _users.GetAllAsync().ConfigureAwait(false);
                PaymentHomeAssociation.MergeAccountEmailsFromUsersIntoHomeSets(allUsers, owned, emailSets);

                foreach (var hid in owned)
                {
                    foreach (var p in snapshot.Where(x => x.HomeId == hid))
                    {
                        if (PaymentHomeAssociation.PayerEmailStillOnHome(p.PayerEmail, hid, emailSets))
                        {
                            byId[p.Id] = p;
                        }
                    }
                }
            }

            return byId.Values.OrderByDescending(p => p.Date ?? DateTime.MinValue).ToList();
        }

        public Task<List<Payment>> GetByHomeIdAsync(Guid homeId)
        {
            lock (_lock)
            {
                var list = _payments.Where(p => p.HomeId == homeId).Select(ClonePayment).ToList();
                return Task.FromResult(list);
            }
        }

        public Task<List<Payment>> GetPayPalSyncPaymentsWithoutHomeIdAsync(DateTime? minDateUtc)
        {
            lock (_lock)
            {
                IEnumerable<Payment> q = _payments.Where(p =>
                    p.PaymentType == Payment.Type.PayPalSync && !p.HomeId.HasValue
                );
                if (minDateUtc.HasValue)
                {
                    var min = minDateUtc.Value;
                    q = q.Where(p => !p.Date.HasValue || p.Date >= min);
                }

                var list = q.Select(ClonePayment).ToList();
                return Task.FromResult(list);
            }
        }

        public Task<HashSet<string>> GetExistingPayPalTransactionIdsAsync(IReadOnlyCollection<string> transactionIds)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (transactionIds == null || transactionIds.Count == 0)
            {
                return Task.FromResult(result);
            }

            var want = new HashSet<string>(
                transactionIds.Where(id => !string.IsNullOrWhiteSpace(id)),
                StringComparer.Ordinal
            );
            lock (_lock)
            {
                foreach (var p in _payments)
                {
                    if (!string.IsNullOrWhiteSpace(p.PayPalTransactionId) && want.Contains(p.PayPalTransactionId))
                    {
                        result.Add(p.PayPalTransactionId);
                    }
                }
            }

            return Task.FromResult(result);
        }

        public Task<Payment> GetByPayPalTransactionIdAsync(string payPalTransactionId)
        {
            lock (_lock)
            {
                var found = _payments.FirstOrDefault(p =>
                    !string.IsNullOrWhiteSpace(p.PayPalTransactionId)
                    && string.Equals(p.PayPalTransactionId, payPalTransactionId, StringComparison.Ordinal)
                );
                return Task.FromResult(found != null ? ClonePayment(found) : null);
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

        public Task<Payment> ReplaceAsync(Payment payment)
        {
            lock (_lock)
            {
                var idx = _payments.FindIndex(p => p.Id == payment.Id);
                if (idx < 0)
                {
                    throw new InvalidOperationException($"Payment {payment.Id} not found.");
                }

                var copy = ClonePayment(payment);
                _payments[idx] = copy;
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
                FullDetailsJSON = p.FullDetailsJSON,
                PayPalTransactionId = p.PayPalTransactionId,
                HomeId = p.HomeId,
            };
        }
    }
}
