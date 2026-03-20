using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Web.Services;

namespace UserPurgeFunction;

public sealed class PurgeTimerFunction
{
    private readonly UserPurgeRunner _runner;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PurgeTimerFunction> _logger;

    public PurgeTimerFunction(
        UserPurgeRunner runner,
        IConfiguration configuration,
        ILogger<PurgeTimerFunction> logger)
    {
        _runner = runner;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Daily purge of users with no owned homes for longer than <c>UserPurge:PurgeAfterDays</c>.
    /// Schedule: app setting <c>UserPurge__Schedule</c> (NCRONTAB, UTC), e.g. <c>0 0 10 * * *</c>.
    /// </summary>
    [Function("UserPurgeTimer")]
    public async Task RunAsync(
        [TimerTrigger("%UserPurge__Schedule%")] TimerInfo timerInfo,
        CancellationToken cancellationToken)
    {
        var options = new UserPurgeOptions
        {
            Enabled = _configuration.GetValue("UserPurge:Enabled", false),
            DryRun = _configuration.GetValue("UserPurge:DryRun", true),
            PurgeAfterDays = _configuration.GetValue("UserPurge:PurgeAfterDays", 30),
            MaxDeletesPerRun = _configuration.GetValue("UserPurge:MaxDeletesPerRun", 100)
        };

        var result = await _runner.RunAsync(options, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "User purge summary: candidates={Candidates}, wouldDelete={WouldDelete}, deleted={Deleted}, skippedAdministrator={SkippedAdmin}, errors={Errors}",
            result.CandidatesFound,
            result.WouldDelete,
            result.Deleted,
            result.SkippedAdministrator,
            result.Errors);
    }
}
