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
    /// Background service that periodically escalates unresolved in-app notifications to throttled email
    /// digests once they have aged past the grace period. The actual sweep lives in
    /// <see cref="NotificationEscalationRunner"/> (a scoped service); this type only owns the enabled
    /// gate and the interval loop, mirroring <see cref="CommitteeMailPoller"/>.
    /// </summary>
    /// <remarks>
    /// May run in the MockData environment: it uses the in-memory repositories and the mock email
    /// transport, so no real email is sent.
    /// </remarks>
    public sealed class NotificationEscalationService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly NotificationEscalationOptions _options;
        private readonly ILogger<NotificationEscalationService> _logger;
        private readonly TimeSpan _sweepInterval;

        public NotificationEscalationService(
            IServiceScopeFactory scopeFactory,
            IOptions<NotificationEscalationOptions> options,
            ILogger<NotificationEscalationService> logger
        )
        {
            _scopeFactory = scopeFactory;
            _options = options.Value;
            _logger = logger;
            _sweepInterval = TimeSpan.FromMinutes(Math.Max(1, _options.SweepIntervalMinutes));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_options.Enabled)
            {
                _logger.LogInformation(
                    "Notification escalation is disabled (NotificationEscalation:Enabled = false)"
                );
                return;
            }

            _logger.LogInformation(
                "Notification escalation started. Sweeping every {Interval} minutes (grace {Grace} min, "
                    + "min digest interval {Digest} h)",
                _sweepInterval.TotalMinutes,
                Math.Max(0, _options.GracePeriodMinutes),
                Math.Max(0, _options.MinDigestIntervalHours)
            );

            // Short initial delay so the app finishes starting up before the first sweep.
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var runner = scope.ServiceProvider.GetRequiredService<NotificationEscalationRunner>();
                    await runner.RunOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unhandled error in notification escalation sweep");
                }

                try
                {
                    await Task.Delay(_sweepInterval, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }
}
