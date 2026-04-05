using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Web.Models;
using Web.Services;
using Web.Services.Cosmos;
using CosmosContainer = Microsoft.Azure.Cosmos.Container;
using CosmosPartitionKey = Microsoft.Azure.Cosmos.PartitionKey;
using CosmosQueryDefinition = Microsoft.Azure.Cosmos.QueryDefinition;

namespace Web.Services.Repositories
{
    public interface IPaymentRepository
    {
        Task<List<Payment>> GetByPayerUniqueIdAsync(string uniqueId);

        /// <summary>
        /// Payments linked to the user, home-scoped PayPal sync rows (when payer email still appears on that home),
        /// and legacy unlinked PayPal rows for this user&apos;s emails.
        /// </summary>
        Task<List<Payment>> GetPaymentsForResidentAsync(User user);

        /// <summary>Returns payments whose <see cref="Payment.HomeId"/> matches (including PayPal sync).</summary>
        Task<List<Payment>> GetByHomeIdAsync(Guid homeId);

        /// <summary>
        /// PayPal-sync payments with no <see cref="Payment.HomeId"/>. When <paramref name="minDateUtc"/> is set,
        /// only rows with <see cref="Payment.Date"/> &gt;= that value (or missing date) are returned.
        /// </summary>
        Task<List<Payment>> GetPayPalSyncPaymentsWithoutHomeIdAsync(DateTime? minDateUtc);

        /// <summary>Returns PayPal transaction ids from <paramref name="transactionIds"/> that already exist in Cosmos.</summary>
        Task<HashSet<string>> GetExistingPayPalTransactionIdsAsync(IReadOnlyCollection<string> transactionIds);

        /// <summary>Returns null if no payment document has this PayPal transaction id.</summary>
        Task<Payment> GetByPayPalTransactionIdAsync(string payPalTransactionId);

        Task<Payment> AddAsync(Payment payment);

        Task<Payment> ReplaceAsync(Payment payment);
    }

    public class CosmosPaymentRepository : IPaymentRepository
    {
        private const int PayPalTransactionIdBatchSize = 50;

        private readonly CosmosContainer _paymentsContainer;
        private readonly IHomeRepository _homes;
        private readonly IResidentRepository _residents;
        private readonly IUserRepository _users;

        public CosmosPaymentRepository(CosmosContainer paymentsContainer, IHomeRepository homes, IResidentRepository residents, IUserRepository users)
        {
            _paymentsContainer = paymentsContainer;
            _homes = homes;
            _residents = residents;
            _users = users;
        }

        public async Task<List<Payment>> GetByPayerUniqueIdAsync(string uniqueId)
        {
            var query = new CosmosQueryDefinition("SELECT * FROM c WHERE c.PayerUniqueId = @uniqueId")
                .WithParameter("@uniqueId", uniqueId);
            var iterator = _paymentsContainer.GetItemQueryIterator<JObject>(query);
            var results = new List<Payment>();
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                results.AddRange(response.Select(CosmosLegacyDocumentMapper.ToPayment));
            }

            return results;
        }

        public async Task<List<Payment>> GetPaymentsForResidentAsync(User user)
        {
            if (user == null)
            {
                return new List<Payment>();
            }

            var byId = new Dictionary<Guid, Payment>();

            foreach (var p in await GetByPayerUniqueIdAsync(user.UniqueId).ConfigureAwait(false))
            {
                byId[p.Id] = p;
            }

            foreach (var raw in UserEmailHelpers.SplitEmails(user.Emails))
            {
                var normalized = UserEmailHelpers.NormalizeEmail(raw);
                if (normalized.Length == 0)
                {
                    continue;
                }

                var query = new CosmosQueryDefinition(@"
SELECT * FROM c WHERE
  (NOT IS_DEFINED(c.HomeId) OR IS_NULL(c.HomeId) OR c.HomeId = '')
  AND (NOT IS_DEFINED(c.PayerUniqueId) OR IS_NULL(c.PayerUniqueId) OR c.PayerUniqueId = '')
  AND LOWER(c.PayerEmail) = @email")
                    .WithParameter("@email", normalized);

                var iterator = _paymentsContainer.GetItemQueryIterator<JObject>(query);
                while (iterator.HasMoreResults)
                {
                    var response = await iterator.ReadNextAsync().ConfigureAwait(false);
                    foreach (var doc in response)
                    {
                        var p = CosmosLegacyDocumentMapper.ToPayment(doc);
                        byId[p.Id] = p;
                    }
                }
            }

            var ownedHomeIds = user.OwnedHomeIds ?? new List<Guid>();
            if (ownedHomeIds.Count > 0)
            {
                var homes = await _homes.GetByIdsAsync(ownedHomeIds).ConfigureAwait(false);
                var ownedResidents = await _residents.GetByHomeIdsAsync(ownedHomeIds).ConfigureAwait(false);
                var residentsByHome = ownedResidents.GroupBy(r => r.HomeId).ToDictionary(g => g.Key, g => g.ToList());
                var emailSets = PaymentHomeAssociation.BuildEmailSetsByHome(homes, residentsByHome);
                var allUsers = await _users.GetAllAsync().ConfigureAwait(false);
                PaymentHomeAssociation.MergeAccountEmailsFromUsersIntoHomeSets(allUsers, ownedHomeIds, emailSets);

                foreach (var p in await QueryPaymentsByHomeIdsAsync(ownedHomeIds).ConfigureAwait(false))
                {
                    if (p.HomeId is Guid hid &&
                        PaymentHomeAssociation.PayerEmailStillOnHome(p.PayerEmail, hid, emailSets))
                    {
                        byId[p.Id] = p;
                    }
                }
            }

            return byId.Values
                .OrderByDescending(p => p.Date ?? DateTime.MinValue)
                .ToList();
        }

        public async Task<List<Payment>> GetByHomeIdAsync(Guid homeId)
        {
            var query = new CosmosQueryDefinition("SELECT * FROM c WHERE c.HomeId = @hid")
                .WithParameter("@hid", homeId.ToString("D"));
            var iterator = _paymentsContainer.GetItemQueryIterator<JObject>(query);
            var results = new List<Payment>();
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync().ConfigureAwait(false);
                results.AddRange(response.Select(CosmosLegacyDocumentMapper.ToPayment));
            }

            return results;
        }

        public async Task<List<Payment>> GetPayPalSyncPaymentsWithoutHomeIdAsync(DateTime? minDateUtc)
        {
            CosmosQueryDefinition query;
            if (minDateUtc.HasValue)
            {
                query = new CosmosQueryDefinition(@"
SELECT * FROM c WHERE
  (NOT IS_DEFINED(c.HomeId) OR IS_NULL(c.HomeId) OR c.HomeId = '')
  AND c.PaymentType = @pt
  AND (NOT IS_DEFINED(c.Date) OR IS_NULL(c.Date) OR c.Date >= @minDate)")
                    .WithParameter("@pt", (int)Payment.Type.PayPalSync)
                    .WithParameter("@minDate", minDateUtc.Value);
            }
            else
            {
                query = new CosmosQueryDefinition(@"
SELECT * FROM c WHERE
  (NOT IS_DEFINED(c.HomeId) OR IS_NULL(c.HomeId) OR c.HomeId = '')
  AND c.PaymentType = @pt")
                    .WithParameter("@pt", (int)Payment.Type.PayPalSync);
            }

            var iterator = _paymentsContainer.GetItemQueryIterator<JObject>(query);
            var results = new List<Payment>();
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync().ConfigureAwait(false);
                results.AddRange(response.Select(CosmosLegacyDocumentMapper.ToPayment));
            }

            return results;
        }

        public async Task<HashSet<string>> GetExistingPayPalTransactionIdsAsync(
            IReadOnlyCollection<string> transactionIds)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (transactionIds == null || transactionIds.Count == 0)
            {
                return result;
            }

            var ids = transactionIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            for (var offset = 0; offset < ids.Count; offset += PayPalTransactionIdBatchSize)
            {
                var batch = ids.Skip(offset).Take(PayPalTransactionIdBatchSize).ToList();
                var paramNames = Enumerable.Range(0, batch.Count).Select(i => $"@t{i}").ToArray();
                var inClause = string.Join(", ", paramNames);
                var q = new CosmosQueryDefinition(
                    $"SELECT VALUE c.PayPalTransactionId FROM c WHERE c.PayPalTransactionId IN ({inClause})");
                for (var j = 0; j < batch.Count; j++)
                {
                    q = q.WithParameter(paramNames[j], batch[j]);
                }

                var iterator = _paymentsContainer.GetItemQueryIterator<string>(q);
                while (iterator.HasMoreResults)
                {
                    var response = await iterator.ReadNextAsync().ConfigureAwait(false);
                    foreach (var id in response)
                    {
                        if (!string.IsNullOrWhiteSpace(id))
                        {
                            result.Add(id);
                        }
                    }
                }
            }

            return result;
        }

        private async Task<List<Payment>> QueryPaymentsByHomeIdsAsync(IReadOnlyList<Guid> homeIds)
        {
            if (homeIds == null || homeIds.Count == 0)
            {
                return new List<Payment>();
            }

            if (homeIds.Count == 1)
            {
                return await GetByHomeIdAsync(homeIds[0]).ConfigureAwait(false);
            }

            var paramNames = Enumerable.Range(0, homeIds.Count).Select(i => $"@h{i}").ToArray();
            var inClause = string.Join(", ", paramNames);
            var query = new CosmosQueryDefinition($"SELECT * FROM c WHERE c.HomeId IN ({inClause})");
            for (var i = 0; i < homeIds.Count; i++)
            {
                query = query.WithParameter(paramNames[i], homeIds[i].ToString("D"));
            }

            var iterator = _paymentsContainer.GetItemQueryIterator<JObject>(query);
            var results = new List<Payment>();
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync().ConfigureAwait(false);
                results.AddRange(response.Select(CosmosLegacyDocumentMapper.ToPayment));
            }

            return results;
        }

        public async Task<Payment> GetByPayPalTransactionIdAsync(string payPalTransactionId)
        {
            if (string.IsNullOrWhiteSpace(payPalTransactionId))
            {
                return null;
            }

            var query = new CosmosQueryDefinition("SELECT * FROM c WHERE c.PayPalTransactionId = @txId")
                .WithParameter("@txId", payPalTransactionId);
            var iterator = _paymentsContainer.GetItemQueryIterator<JObject>(query);
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                var first = response.FirstOrDefault();
                if (first != null)
                {
                    return CosmosLegacyDocumentMapper.ToPayment(first);
                }
            }

            return null;
        }

        public async Task<Payment> AddAsync(Payment payment)
        {
            if (payment.Id == Guid.Empty)
            {
                payment.Id = Guid.NewGuid();
            }

            var doc = CosmosLegacyDocumentMapper.ToPaymentDocument(payment);
            // Matches legacy EF Core Cosmos containers that use the default single-partition ("NoPartitionKey") layout.
            await _paymentsContainer.CreateItemAsync(doc, CosmosPartitionKey.None);
            return payment;
        }

        public async Task<Payment> ReplaceAsync(Payment payment)
        {
            var doc = CosmosLegacyDocumentMapper.ToPaymentDocument(payment);
            var docId = CosmosLegacyDocumentMapper.ToPaymentDocumentId(payment.Id);
            await _paymentsContainer.ReplaceItemAsync(doc, docId, CosmosPartitionKey.None).ConfigureAwait(false);
            return payment;
        }
    }
}
