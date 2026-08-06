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

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly UserPurgeOptions _options;
        private readonly ILogger<UserPurgeService> _logger;
        private readonly TimeSpan _interval;
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
            _interval = JobInterval.FromHours(_options.IntervalHours);
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
                var wait = _interval;
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
        /// Purges if the interval has elapsed since the last successful run, and returns how long to wait
        /// before checking again.
        /// </summary>
        internal async Task<TimeSpan> RunIfDueAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var scope = _scopeFactory.CreateScope();
            var jobState = scope.ServiceProvider.GetRequiredService<IBackgroundJobStateRepository>();

            var state =
                await jobState.GetAsync(JobName).ConfigureAwait(false)
                ?? new BackgroundJobState { JobName = JobName };

            // Paced on the *attempt*, not the success. A sweep that is interrupted partway (host shutdown
            // during a deploy cancels UserPurgeRunner between candidates) has already performed some
            // irreversible deletions, so letting the next start run again with a fresh MaxDeletesPerRun
            // budget would restore exactly the per-restart cap this durable state exists to prevent.
            var elapsed = DateTime.UtcNow - state.LastAttemptUtc;
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
            if (result.Errors == 0)
            {
                state.LastSuccessUtc = DateTime.UtcNow;
                await jobState.UpsertAsync(state).ConfigureAwait(false);
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
    }
}
