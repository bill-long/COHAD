#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Web.Configuration;

namespace Web.Services
{
    /// <summary>
    /// Periodically reconciles the COHAD suppression list against Postmark's stream suppression
    /// dumps (issue #9). The sweep itself lives in <see cref="PostmarkSuppressionSyncRunner"/>
    /// (scoped); this type only owns the enabled gate and the interval loop, mirroring
    /// <see cref="UserPurgeService"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately keeps no durable schedule state, for the same reason as the user purge: the
    /// reconciliation is idempotent (deterministic evidence keys) and additive, so running more
    /// often than configured costs two tiny HTTP reads and running less often only defers the same
    /// records to the next run.
    /// </remarks>
    public sealed class PostmarkSuppressionSyncService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly PostmarkSuppressionSyncOptions _options;
        private readonly PostmarkOptions _postmarkOptions;
        private readonly IHostEnvironment _env;
        private readonly ILogger<PostmarkSuppressionSyncService> _logger;
        private readonly TimeSpan _interval;
        private readonly TimeSpan _startupDelay;

        public PostmarkSuppressionSyncService(
            IServiceScopeFactory scopeFactory,
            IOptions<PostmarkSuppressionSyncOptions> options,
            IOptions<PostmarkOptions> postmarkOptions,
            IHostEnvironment env,
            ILogger<PostmarkSuppressionSyncService> logger
        )
        {
            _scopeFactory = scopeFactory;
            _options = options.Value;
            _postmarkOptions = postmarkOptions.Value;
            _env = env;
            _logger = logger;
            _interval = JobInterval.FromHours(_options.IntervalHours);
            _startupDelay = JobInterval.FromSeconds(_options.StartupDelaySeconds);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_options.Enabled)
            {
                _logger.LogInformation(
                    "Postmark suppression sync is disabled (Postmark:SuppressionSync:Enabled = false)"
                );
                return;
            }

            // The dump API authenticates with the same server token the SMTP transport uses. In
            // MockData the dump client is an in-memory fake that needs no token, so the check is
            // skipped there. In Development it is NOT skipped (unlike the webhook controller's
            // verifier, which treats Development as unauthenticated-by-default): a sync enabled
            // without a token can never work, so it fails fast here rather than erroring on
            // every interval.
            if (!_env.IsEnvironment("MockData") && string.IsNullOrWhiteSpace(_postmarkOptions.ServerToken))
            {
                _logger.LogError(
                    "Postmark suppression sync is enabled but Postmark:ServerToken is missing or empty - not running. "
                        + "Set it via user secrets or environment variable (Postmark__ServerToken) "
                        + "and restart the app; this service does not re-check while running."
                );
                return;
            }

            _logger.LogInformation(
                "Postmark suppression sync started. Running every {Interval} hours.",
                _interval.TotalHours
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
                // Run first, then wait (the UserPurgeService invariant): delaying first would mean
                // a deploy cadence shorter than the interval silently prevents the reconciliation
                // from ever running. Running more often is free - reruns are evidence-key no-ops.
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var runner = scope.ServiceProvider.GetRequiredService<PostmarkSuppressionSyncRunner>();
                    var result = await runner.RunAsync(stoppingToken);

                    _logger.LogInformation(
                        "Postmark suppression sync summary: streams={Streams}, dumped={Dumped}, "
                            + "alreadySuppressed={AlreadySuppressed}, recorded={Recorded}, errors={Errors}",
                        result.StreamsQueried,
                        result.DumpedAddresses,
                        result.AlreadySuppressed,
                        result.Recorded,
                        result.Errors
                    );
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unhandled error in Postmark suppression sync run");
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
