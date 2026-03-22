using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Web.Services;

namespace UserPurgeFunction;

public sealed class PayPalSyncTimerFunction
{
    private readonly PayPalPaymentSyncRunner _runner;
    private readonly IOptions<PayPalOptions> _payPalOptions;
    private readonly ILogger<PayPalSyncTimerFunction> _logger;

    public PayPalSyncTimerFunction(
        PayPalPaymentSyncRunner runner,
        IOptions<PayPalOptions> payPalOptions,
        ILogger<PayPalSyncTimerFunction> logger)
    {
        _runner = runner;
        _payPalOptions = payPalOptions;
        _logger = logger;
    }

    /// <summary>
    /// Weekly import of PayPal Transaction Search rows into Cosmos (linked to users when email matches; otherwise unlinked).
    /// Schedule: app setting <c>PayPalSyncSchedule</c> (NCRONTAB, UTC), e.g. <c>0 0 6 * * 1</c> (Mondays 06:00 UTC).
    /// Enable with <c>PayPal__SyncEnabled=true</c> and set <c>PayPal__ClientId</c> / <c>PayPal__ClientSecret</c>.
    /// </summary>
    [Function("PayPalSyncTimer")]
    public async Task RunAsync(
        [TimerTrigger("%PayPalSyncSchedule%")] TimerInfo timerInfo,
        CancellationToken cancellationToken)
    {
        if (!_payPalOptions.Value.SyncEnabled)
        {
            _logger.LogInformation("PayPal sync is disabled (PayPal:SyncEnabled).");
            return;
        }

        if (string.IsNullOrWhiteSpace(_payPalOptions.Value.ClientId) ||
            string.IsNullOrWhiteSpace(_payPalOptions.Value.ClientSecret))
        {
            _logger.LogWarning("PayPal sync is enabled but PayPal:ClientId or PayPal:ClientSecret is missing.");
            return;
        }

        var result = await _runner.RunAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "PayPal sync: read={Read}, inserted={Inserted}, insertedUnlinked={Unlinked}, existingLinked={ExistingLinked}, skippedNoPayerEmail={NoEmail}, skippedDuplicate={Dup}, skippedFiltered={Filtered}",
            result.TransactionsRead,
            result.Inserted,
            result.InsertedUnlinked,
            result.ExistingLinked,
            result.SkippedNoPayerEmail,
            result.SkippedDuplicate,
            result.SkippedFiltered);
    }
}
