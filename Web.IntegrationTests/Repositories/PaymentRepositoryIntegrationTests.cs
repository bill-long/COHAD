using Web.IntegrationTests.Support;
using Web.Models;

namespace Web.IntegrationTests.Repositories;

[Collection("Cosmos integration")]
public sealed class PaymentRepositoryIntegrationTests
{
    private readonly CosmosIntegrationFixture _fixture;

    public PaymentRepositoryIntegrationTests(CosmosIntegrationFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Add_and_query_by_payer_round_trips_without_partition_key_errors()
    {
        Skip.IfNot(_fixture.IsAvailable, _fixture.UnavailableReason);

        var repo = _fixture.CreatePaymentRepository();
        var payerUniqueId = $"google.com{Guid.NewGuid():N}";
        var marker = Guid.NewGuid().ToString("N");
        await repo.AddAsync(new Payment
        {
            Id = Guid.NewGuid(),
            PayerUniqueId = payerUniqueId,
            PayerEmail = "payer@example.com",
            PayerName = "Payer",
            Amount = "10.00",
            Date = DateTime.UtcNow,
            PaymentType = Payment.Type.OneTime,
            FullDetailsJSON = $"{{\"marker\":\"{marker}\"}}"
        }).ConfigureAwait(false);

        var list = await repo.GetByPayerUniqueIdAsync(payerUniqueId).ConfigureAwait(false);
        Assert.Contains(list, p => p.FullDetailsJSON != null && p.FullDetailsJSON.Contains(marker, StringComparison.Ordinal));
    }
}
