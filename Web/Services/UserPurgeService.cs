#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Web.Models;
using Web.Services.Repositories;

namespace Web.Services
{
    /// <summary>
    /// Background service that periodically purges users who have had no owned homes or roles for longer
    /// than <see cref="UserPurgeOptions.PurgeAfterDays"/>. The sweep itself lives in
    /// <see cref="UserPurgeRunner"/> (scoped); this type owns the enabled gate, the pacing, and the loop.
    /// </summary>
    /// <remarks>
    /// Paces from durable <see cref="BackgroundJobState"/> rather than an in-process timer alone. A purely
    /// in-process interval would restart with the process, so on a host that redeploys or recycles several
    /// times a day the purge would run on every start - turning
    /// <see cref="UserPurgeOptions.MaxDeletesPerRun"/> from a per-day cap on irreversible deletions into a
    /// per-restart cap. Consulting persisted state keeps the cap meaningful while still checking on startup,
    /// so an interval can never be silently skipped the way a fixed cron occurrence can.
    /// </remarks>
    public sealed class UserPurgeService : BackgroundService
    {
        /// <summary>Key for this job's <see cref="BackgroundJobState"/> document.</summary>
        public const string JobName = "user-purge";

        /// <summary>
        /// Pacing key used while <see cref="UserPurgeOptions.DryRun"/> is set. Kept separate so a dry
        /// sweep neither consumes the live interval nor is blocked by one a live sweep consumed.
        /// </summary>
        public const string DryRunJobName = "user-purge-dryrun";

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly UserPurgeOptions _options;
        private readonly ILogger<UserPurgeService> _logger;
        private readonly TimeSpan _interval;
        private readonly TimeSpan _tickInterval;
        private readonly TimeSpan _startupDelay;

        public UserPurgeService(
            IServiceScopeFactory scopeFactory,
            IOptions<UserPurgeOptions> options,
            ILogger<UserPurgeService> logger
        )
        {
            _scopeFactory = scopeFactory;
            _options = options.Value;
            _logger = logger;
            // A comparison window, not a Task.Delay argument - the loop waits at most _tickInterval.
            // Building it with the delay-capped family would silently cap a quarterly IntervalHours at
            // ~49.7 days and purge twice as often as configured.
            _interval = JobInterval.WindowFromHours(_options.IntervalHours);
            _tickInterval = JobInterval.FromMinutes(_options.TickIntervalMinutes);
            _startupDelay = JobInterval.FromSeconds(_options.StartupDelaySeconds);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_options.Enabled)
            {
                _logger.LogInformation("User purge is disabled (UserPurge:Enabled = false)");
                return;
            }

            _logger.LogInformation(
                "User purge started. Running at most every {Interval} hours (purgeAfterDays={Days}, dryRun={DryRun})",
                _interval.TotalHours,
                Math.Max(1, _options.PurgeAfterDays),
                _options.DryRun
            );

            // Short initial delay so the app finishes starting up before the first check.
            try
            {
                await Task.Delay(_startupDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                // Check before waiting, so a restart can never skip an interval. Whether the check
                // actually purges is decided by persisted state, so restarting often does not purge often.
                //
                // On failure fall back to the *tick* interval, never the pacing interval: nothing ran and
                // no state was consumed, so a single transient Cosmos read failure must not postpone the
                // next check by a full IntervalHours (which for a quarterly cadence is seven weeks).
                var wait = _tickInterval;
                try
                {
                    wait = await RunIfDueAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unhandled error in user purge run");
                }

                // Never sleep past the tick interval either: the pacing decision is cheap and re-checking
                // keeps the loop responsive to a state document changed out of band.
                if (wait > _tickInterval)
                    wait = _tickInterval;

                try
                {
                    await Task.Delay(JobInterval.Clamp(wait), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        /// <summary>
        /// Purges if the interval has elapsed since the last <em>attempt</em>, and returns how long to wait
        /// before checking again. Pacing on the attempt rather than the last success is deliberate: see the
        /// remarks on <see cref="UserPurgeService"/>.
        /// </summary>
        internal async Task<TimeSpan> RunIfDueAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var scope = _scopeFactory.CreateScope();
            var jobState = scope.ServiceProvider.GetRequiredService<IBackgroundJobStateRepository>();

            // A dry sweep paces on its own key. Sharing the live key would either let a dry run consume
            // the live interval (delaying real purges) or let a live run silence the dry run the cut-over
            // runbook tells the operator to verify with. It still paces, so a host that recycles several
            // times a day does not re-scan the Users container on every start.
            var jobName = _options.DryRun ? DryRunJobName : JobName;

            var state =
                await jobState.GetAsync(jobName).ConfigureAwait(false)
                ?? new BackgroundJobState { JobName = jobName };

            // Paced on the *attempt*, not the success. A sweep interrupted partway (host shutdown during a
            // deploy cancels UserPurgeRunner between candidates) has already performed some irreversible
            // deletions, so letting the next start run again with a fresh MaxDeletesPerRun budget would
            // restore the per-restart cap this state exists to prevent.
            var elapsed = ElapsedSince(state.LastAttemptUtc, nameof(state.LastAttemptUtc));
            if (elapsed < _interval)
                return _interval - elapsed;

            // Stamp the attempt before deleting anything, so an interrupted sweep still consumes the
            // interval. Mirrors PayPalSyncScheduler.
            state.LastAttemptUtc = DateTime.UtcNow;
            await jobState.UpsertAsync(state).ConfigureAwait(false);

            var runner = scope.ServiceProvider.GetRequiredService<UserPurgeRunner>();
            var result = await runner.RunAsync(_options, cancellationToken).ConfigureAwait(false);

            // LastSuccessUtc records only a clean sweep. Per-candidate failures are counted rather than
            // thrown, so stamping it unconditionally would report a wholly failed purge (deleted=0,
            // errors=100) as a healthy run to anyone inspecting the state document.
            //
            // The sweep has already committed irreversible deletions by this point, so a failure to write
            // the health stamp must not propagate: it would be logged as "unhandled error in user purge"
            // and suppress the summary below, telling the operator nothing was deleted when it was.
            if (result.Errors == 0)
            {
                state.LastSuccessUtc = DateTime.UtcNow;
                try
                {
                    await jobState.UpsertAsync(state).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "User purge completed but its success timestamp could not be persisted");
                }
            }

            _logger.LogInformation(
                "User purge summary: candidates={Candidates}, wouldDelete={WouldDelete}, deleted={Deleted}, "
                    + "skippedAdministrator={SkippedAdmin}, errors={Errors}",
                result.CandidatesFound,
                result.WouldDelete,
                result.Deleted,
                result.SkippedAdministrator,
                result.Errors
            );

            return _interval;
        }

        /// <summary>
        /// Time since a pacing stamp. A future-dated stamp (clock skew, or a state document restored from
        /// another environment) would make every comparison negative and latch the job off silently, so it
        /// is reported as fully elapsed and warned about instead.
        /// </summary>
        private TimeSpan ElapsedSince(DateTime stampUtc, string field)
        {
            var elapsed = DateTime.UtcNow - stampUtc;
            if (elapsed >= TimeSpan.Zero)
                return elapsed;

            _logger.LogWarning(
                "User purge state has a future-dated {Field} ({Stamp:o}); treating the purge as due",
                field,
                stampUtc
            );
            return TimeSpan.MaxValue;
        }
    }
}
