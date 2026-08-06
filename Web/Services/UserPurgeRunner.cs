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

    /// <summary>
    /// How long <see cref="UserPurgeService"/> waits between sweeps. Consumed by the hosted service, not
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
        cancellationToken.ThrowIfCancellationRequested();

        var result = new UserPurgeResult();
        if (options == null || !options.Enabled)
        {
            _logger.LogInformation("User purge is disabled; skipping run.");
            return result;
        }

        // Clamped once: the audit text below must state the retention rule that was actually applied.
        var purgeAfterDays = Math.Max(1, options.PurgeAfterDays);
        var cutoff = DateTime.UtcNow.AddDays(-purgeAfterDays);

        var candidates = await _userRepository.GetPurgeCandidatesAsync(cutoff).ConfigureAwait(false);
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

            // Audited after the delete succeeded, deliberately, and this ordering should not be flipped
            // again without reading this note. Both orderings lose something, because the two writes go to
            // different containers with no transaction between them:
            //
            //   audit first  -> a failed delete leaves a permanent entry asserting a purge that did not
            //                   happen, which cannot be distinguished from a real one by reading the log;
            //   delete first -> a failed audit leaves a deletion recorded only in the error log below.
            //
            // The second is preferred: the audit log then only ever describes deletions that actually
            // occurred, and the gap is both visible and recoverable from the error log, which names the
            // user. An unfalsifiable false entry is worse than a gap that says where to look.
            try
            {
                await _auditLogRepository
                    .AddAsync(
                        new NewAuditLogEntry
                        {
                            Id = Guid.NewGuid(),
                            SubjectId = user.UniqueId,
                            SubjectName = user.Emails,
                            Action = $"Purged inactive user (no homes or no roles for {purgeAfterDays}+ days).",
                            Time = DateTime.UtcNow,
                            UserDisplayName = "System",
                            UserId = "user-purge",
                        }
                    )
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result.Errors++;
                // UniqueId only: this job exists to remove stale personal data, so it must not copy the
                // addresses into Application Insights, which has its own retention and which the purge
                // cannot clean up. The id is enough to reconstruct what was deleted.
                _logger.LogError(
                    ex,
                    "Purged user {UniqueId} but failed to write the audit entry; this log line is the only "
                        + "record of that deletion",
                    user.UniqueId
                );
            }
        }

        return result;
    }
}
