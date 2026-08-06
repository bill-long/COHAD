#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Web.Models;
using Web.Services.Repositories;

namespace Web.Services
{
    /// <summary>
    /// Decides whether a PayPal sync is due and runs it, pacing from durable state rather than an
    /// in-process timer. The sync interval (a week by default) is far longer than the app's typical
    /// uptime between deployments, so an in-process timer alone would rarely reach its next occurrence.
    /// </summary>
    /// <remarks>
    /// The sync's own date window is a rolling <c>[now - SyncLookbackDays, now)</c> computed inside
    /// <see cref="PayPalPaymentSyncRunner"/>, so the persisted timestamps are purely pacing state and
    /// never a correctness input. Losing them re-runs the sync, which dedupes.
    /// </remarks>
    public sealed class PayPalSyncScheduler
    {
        /// <summary>Key for this job's <see cref="BackgroundJobState"/> document.</summary>
        public const string JobName = "paypal-sync";

        private readonly IPayPalPaymentSyncRunner _runner;
        private readonly IBackgroundJobStateRepository _jobState;
        private readonly IOptions<PayPalOptions> _options;
        private readonly ILogger<PayPalSyncScheduler> _logger;

        public PayPalSyncScheduler(
            IPayPalPaymentSyncRunner runner,
            IBackgroundJobStateRepository jobState,
            IOptions<PayPalOptions> options,
            ILogger<PayPalSyncScheduler> logger
        )
        {
            _runner = runner;
            _jobState = jobState;
            _options = options;
            _logger = logger;
        }

        /// <summary>
        /// Runs the sync if it is due, and returns whether it ran. Exceptions from the sync itself
        /// propagate to the caller; the attempt is recorded first so a failure still paces the retry.
        /// </summary>
        public async Task<bool> RunIfDueAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var opt = _options.Value;
            if (!opt.SyncEnabled)
                return false;

            if (string.IsNullOrWhiteSpace(opt.ClientId) || string.IsNullOrWhiteSpace(opt.ClientSecret))
            {
                _logger.LogWarning("PayPal sync is enabled but PayPal:ClientId or PayPal:ClientSecret is missing.");
                return false;
            }

            var state =
                await _jobState.GetAsync(JobName).ConfigureAwait(false)
                ?? new BackgroundJobState { JobName = JobName };

            var now = DateTime.UtcNow;
            var syncInterval = JobInterval.FromDays(opt.SyncIntervalDays);
            if (now - state.LastSuccessUtc < syncInterval)
                return false;

            // A run is due, but if the last attempt failed recently, back off instead of retrying on
            // every tick - otherwise a bad credential means an API call every check interval, forever.
            var retryInterval = JobInterval.FromHours(opt.SyncRetryIntervalHours);
            if (now - state.LastAttemptUtc < retryInterval)
                return false;

            // Record the attempt before running so a failure (or a crash mid-run) still paces the retry.
            state.LastAttemptUtc = now;
            await _jobState.UpsertAsync(state).ConfigureAwait(false);

            var result = await _runner.RunAsync(cancellationToken).ConfigureAwait(false);

            // The sync itself has already committed its payments. If persisting the success stamp fails,
            // report it loudly but do not rethrow: letting it propagate would log a completed run as a
            // failure. The run is still re-attempted once the retry interval elapses, because
            // LastSuccessUtc stayed stale - bounded re-work, never a lost import.
            state.LastSuccessUtc = DateTime.UtcNow;
            try
            {
                await _jobState.UpsertAsync(state).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(
                    ex,
                    "PayPal sync completed but its success timestamp could not be persisted; the sync will "
                        + "re-run after {Retry} h until a write succeeds",
                    retryInterval.TotalHours
                );
            }

            _logger.LogInformation(
                "PayPal sync: read={Read}, inserted={Inserted}, insertedUnlinked={Unlinked}, "
                    + "existingLinked={ExistingLinked}, skippedNoPayerEmail={NoEmail}, skippedDuplicate={Dup}, "
                    + "skippedFiltered={Filtered}",
                result.TransactionsRead,
                result.Inserted,
                result.InsertedUnlinked,
                result.ExistingLinked,
                result.SkippedNoPayerEmail,
                result.SkippedDuplicate,
                result.SkippedFiltered
            );

            return true;
        }
    }
}
