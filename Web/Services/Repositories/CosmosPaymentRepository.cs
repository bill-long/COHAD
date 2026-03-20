using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Web.Models;
using Web.Services.Cosmos;
using CosmosContainer = Microsoft.Azure.Cosmos.Container;
using CosmosPartitionKey = Microsoft.Azure.Cosmos.PartitionKey;
using CosmosQueryDefinition = Microsoft.Azure.Cosmos.QueryDefinition;

namespace Web.Services.Repositories
{
    public interface IPaymentRepository
    {
        Task<List<Payment>> GetByPayerUniqueIdAsync(string uniqueId);
        Task<Payment> AddAsync(Payment payment);
    }

    public class CosmosPaymentRepository : IPaymentRepository
    {
        private readonly CosmosContainer _paymentsContainer;

        public CosmosPaymentRepository(CosmosContainer paymentsContainer)
        {
            _paymentsContainer = paymentsContainer;
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
    }
}
