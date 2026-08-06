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
    /// Background service that wakes on a short interval and asks <see cref="PayPalSyncScheduler"/>
    /// whether a PayPal sync is due. All pacing lives in the scheduler (and its durable state); this
    /// type only owns the enabled gate and the tick loop, mirroring <see cref="NotificationEscalationService"/>.
    /// </summary>
    public sealed class PayPalSyncService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly PayPalOptions _options;
        private readonly ILogger<PayPalSyncService> _logger;
        private readonly TimeSpan _checkInterval;
        private readonly TimeSpan _startupDelay;

        public PayPalSyncService(
            IServiceScopeFactory scopeFactory,
            IOptions<PayPalOptions> options,
            ILogger<PayPalSyncService> logger
        )
        {
            _scopeFactory = scopeFactory;
            _options = options.Value;
            _logger = logger;
            _checkInterval = JobInterval.FromMinutes(_options.SyncCheckIntervalMinutes);
            _startupDelay = JobInterval.FromSeconds(_options.StartupDelaySeconds);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_options.SyncEnabled)
            {
                _logger.LogInformation("PayPal sync is disabled (PayPal:SyncEnabled = false)");
                return;
            }

            _logger.LogInformation(
                "PayPal sync started. Checking every {Check} minutes; syncs at most every {Interval} days "
                    + "(retry after {Retry} h on failure)",
                _checkInterval.TotalMinutes,
                Math.Max(1, _options.SyncIntervalDays),
                Math.Max(1, _options.SyncRetryIntervalHours)
            );

            // Short initial delay so the app finishes starting up before the first check. Without it a
            // cold start with an elapsed interval runs the full sync (repository scans plus paged PayPal
            // calls) while the app is still serving its first requests.
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
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var scheduler = scope.ServiceProvider.GetRequiredService<PayPalSyncScheduler>();
                    await scheduler.RunIfDueAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unhandled error in PayPal sync");
                }

                try
                {
                    await Task.Delay(_checkInterval, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }
}
