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

            var credentialsMissing =
                string.IsNullOrWhiteSpace(opt.ClientId) || string.IsNullOrWhiteSpace(opt.ClientSecret);

            BackgroundJobState state;
            try
            {
                state =
                    await _jobState.GetAsync(JobName).ConfigureAwait(false)
                    ?? new BackgroundJobState { JobName = JobName };
            }
            catch (Exception ex) when (ex is not OperationCanceledException && credentialsMissing)
            {
                // A fresh cut-over is likely to be missing the credentials *and* the out-of-band
                // BackgroundJobState container at once. Without this, the state read throws first and the
                // operator only ever sees a Cosmos stack trace, fixes the container, and only then
                // discovers the second missing piece - one wasted debug cycle.
                _logger.LogWarning(
                    "PayPal sync is enabled but PayPal:ClientId or PayPal:ClientSecret is missing, and the "
                        + "job-state read also failed. Both need fixing."
                );
                throw;
            }

            var now = DateTime.UtcNow;
            var syncInterval = JobInterval.WindowFromDays(opt.SyncIntervalDays);
            if (ElapsedSince(state.LastSuccessUtc, nameof(state.LastSuccessUtc)) < syncInterval)
                return false;

            // A run is due. If the last attempt failed, back off instead of retrying on every tick, so a
            // bad credential cannot mean an API call every check interval forever.
            //
            // The gate reads the persisted LastAttemptFailed flag rather than comparing the two stamps:
            // LastAttemptUtc is written for successful runs too, so gating every attempt on it would let a
            // SyncRetryIntervalHours larger than SyncIntervalDays silently override the configured cadence,
            // while deriving "failed" from LastAttemptUtc > LastSuccessUtc inverts under a future-dated
            // stamp and would drop the backoff entirely in exactly that case.
            var retryInterval = JobInterval.WindowFromHours(opt.SyncRetryIntervalHours);
            if (
                state.LastAttemptFailed
                && ElapsedSince(state.LastAttemptUtc, nameof(state.LastAttemptUtc)) < retryInterval
            )
                return false;

            // Record the attempt before running, marked failed until proven otherwise, so a failure (or a
            // crash mid-run) still paces the retry. Missing credentials count as a failed attempt
            // deliberately: this stamp is what backs the warning below off to once per
            // SyncRetryIntervalHours instead of once per tick - ~8,700 traces a year on an hourly tick.
            state.LastAttemptUtc = now;
            state.LastAttemptFailed = true;
            await _jobState.UpsertAsync(state).ConfigureAwait(false);

            if (credentialsMissing)
            {
                _logger.LogWarning(
                    "PayPal sync is enabled but PayPal:ClientId or PayPal:ClientSecret is missing. "
                        + "Retrying in {Retry} h.",
                    retryInterval.TotalHours
                );
                return false;
            }

            var result = await _runner.RunAsync(cancellationToken).ConfigureAwait(false);

            // The sync itself has already committed its payments. If persisting the success stamp fails,
            // report it loudly but do not rethrow: letting it propagate would log a completed run as a
            // failure. The run is still re-attempted once the retry interval elapses, because
            // LastSuccessUtc stayed stale - bounded re-work, never a lost import.
            state.LastSuccessUtc = DateTime.UtcNow;
            state.LastAttemptFailed = false;
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

        /// <summary>
        /// Time since a pacing stamp. A future-dated stamp (clock skew, or a state document restored from
        /// another environment) would make every comparison negative and latch the sync off silently, so
        /// it is reported as fully elapsed and warned about instead.
        /// </summary>
        private TimeSpan ElapsedSince(DateTime stampUtc, string field)
        {
            var elapsed = DateTime.UtcNow - stampUtc;
            if (elapsed >= TimeSpan.Zero)
                return elapsed;

            _logger.LogWarning(
                "PayPal sync state has a future-dated {Field} ({Stamp:o}); treating the sync as due",
                field,
                stampUtc
            );
            return TimeSpan.MaxValue;
        }
    }
}
