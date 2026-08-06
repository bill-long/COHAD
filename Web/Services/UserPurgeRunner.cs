using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Web.Models;
using Web.Services.Repositories;

namespace Web.Services;

public sealed class UserPurgeOptions
{
    public bool Enabled { get; set; }

    /// <summary>
    /// Log candidates without deleting. Defaults to <c>true</c> so that a config source supplying
    /// <see cref="Enabled"/> but omitting this key cannot perform irreversible deletions; the deleted
    /// Function host enforced the same fail-safe at its read site.
    /// </summary>
    public bool DryRun { get; set; } = true;

    public int PurgeAfterDays { get; set; } = 30;

    public int MaxDeletesPerRun { get; set; } = 100;

    /// <summary>
    /// How long <see cref="UserPurgeService"/> waits between runs. Consumed by the hosted service, not
    /// by <see cref="UserPurgeRunner"/> itself.
    /// </summary>
    public int IntervalHours { get; set; } = 24;

    /// <summary>
    /// Delay before the first run after startup, so the app finishes booting first. Consumed by the
    /// hosted service; tests set it to zero.
    /// </summary>
    public int StartupDelaySeconds { get; set; } = 15;
}

public sealed class UserPurgeResult
{
    public int CandidatesFound { get; set; }

    public int SkippedAdministrator { get; set; }

    public int Deleted { get; set; }

    public int WouldDelete { get; set; }

    public int Errors { get; set; }
}

/// <summary>
/// Removes users who have had no owned homes or no roles for longer than <see cref="UserPurgeOptions.PurgeAfterDays"/>.
/// </summary>
public sealed class UserPurgeRunner
{
    private readonly IUserRepository _userRepository;
    private readonly IAuditLogRepository _auditLogRepository;
    private readonly ILogger<UserPurgeRunner> _logger;

    public UserPurgeRunner(
        IUserRepository userRepository,
        IAuditLogRepository auditLogRepository,
        ILogger<UserPurgeRunner> logger
    )
    {
        _userRepository = userRepository;
        _auditLogRepository = auditLogRepository;
        _logger = logger;
    }

    public async Task<UserPurgeResult> RunAsync(UserPurgeOptions options, CancellationToken cancellationToken = default)
    {
        var result = new UserPurgeResult();
        if (options == null || !options.Enabled)
        {
            _logger.LogInformation("User purge is disabled; skipping run.");
            return result;
        }

        var max = Math.Max(1, options.MaxDeletesPerRun);
        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, options.PurgeAfterDays));

        var candidates = await _userRepository.GetPurgeCandidatesAsync(cutoff, max).ConfigureAwait(false);
        result.CandidatesFound = candidates.Count;

        foreach (var user in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (user.Roles != null && user.Roles.Contains(User.Role.Administrator))
            {
                result.SkippedAdministrator++;
                continue;
            }

            if (options.DryRun)
            {
                result.WouldDelete++;
                _logger.LogInformation("DryRun: would purge user {UniqueId}", user.UniqueId);
                continue;
            }

            try
            {
                await _userRepository.DeleteAsync(user.UniqueId).ConfigureAwait(false);
                result.Deleted++;
                _logger.LogInformation("Purged user {UniqueId}", user.UniqueId);
            }
            catch (Exception ex)
            {
                result.Errors++;
                _logger.LogError(ex, "Failed to purge user {UniqueId}", user.UniqueId);
                continue;
            }

            // Audited only after the delete actually succeeded. Writing the entry first would leave a
            // permanent "Purged inactive user" record for an account that is still present whenever the
            // delete fails, which is worse than a gap: an audit log that lies cannot be reconciled.
            try
            {
                await _auditLogRepository
                    .AddAsync(
                        new NewAuditLogEntry
                        {
                            Id = Guid.NewGuid(),
                            SubjectId = user.UniqueId,
                            SubjectName = user.Emails,
                            Action = $"Purged inactive user (no homes or no roles for {options.PurgeAfterDays}+ days).",
                            Time = DateTime.UtcNow,
                            UserDisplayName = "System",
                            UserId = "user-purge",
                        }
                    )
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The user is already gone, so Deleted stays counted - but this still counts as an error.
                // The summary's error count is the only aggregate the job emits; reporting errors=0 for a
                // sweep that deleted accounts without auditing them would read as a clean run.
                result.Errors++;
                _logger.LogError(ex, "Purged user {UniqueId} but failed to write the audit entry", user.UniqueId);
            }
        }

        return result;
    }
}
