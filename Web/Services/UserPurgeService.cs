#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Web.Services
{
    /// <summary>
    /// Background service that periodically purges users who have had no owned homes or roles for longer
    /// than <see cref="UserPurgeOptions.PurgeAfterDays"/>. The sweep itself lives in
    /// <see cref="UserPurgeRunner"/> (scoped); this type only owns the enabled gate and the interval loop.
    /// </summary>
    /// <remarks>
    /// Deliberately keeps no durable schedule state. The purge selects candidates against a rolling
    /// <c>UtcNow</c> cutoff and deletes them, so a repeat run inside the same interval finds nothing and
    /// costs one query, while missing an interval simply defers the same candidates to the next run.
    /// Over-running is free and under-running is harmless, so in-process pacing is sufficient.
    /// <para>
    /// This is only true because the sweep is unbounded. An earlier revision capped deletions per run,
    /// which made "how often did this run" load-bearing and pulled durable pacing state, a separate dry-run
    /// key, and interrupted-sweep accounting in behind it. The cap was removed instead: at this
    /// association's scale, a sweep large enough for a cap to matter is a data-loss event whose remedy is
    /// restoring a backup, not deleting the same accounts more slowly.
    /// </para>
    /// </remarks>
    public sealed class UserPurgeService : BackgroundService
    {
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
                "User purge started. Running every {Interval} hours (purgeAfterDays={Days}, dryRun={DryRun})",
                _interval.TotalHours,
                Math.Max(1, _options.PurgeAfterDays),
                _options.DryRun
            );

            // Short initial delay so the app finishes starting up before the first run.
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
                // Run first, then wait. Delaying first would mean that a deploy cadence shorter than the
                // interval silently prevents the purge from ever running, since each restart resets the
                // timer. Running first inverts that: frequent deploys run it more often, which is free.
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var runner = scope.ServiceProvider.GetRequiredService<UserPurgeRunner>();
                    var result = await runner.RunAsync(_options, stoppingToken);

                    _logger.LogInformation(
                        "User purge summary: candidates={Candidates}, wouldDelete={WouldDelete}, deleted={Deleted}, "
                            + "skippedAdministrator={SkippedAdmin}, errors={Errors}",
                        result.CandidatesFound,
                        result.WouldDelete,
                        result.Deleted,
                        result.SkippedAdministrator,
                        result.Errors
                    );
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
                    await Task.Delay(_interval, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }
}
