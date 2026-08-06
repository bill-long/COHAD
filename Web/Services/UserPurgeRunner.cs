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
    /// Minimum hours between purge sweeps. Enforced against durable state, not an in-process timer.
    /// Consumed by the hosted service, not by <see cref="UserPurgeRunner"/> itself.
    /// </summary>
    public int IntervalHours { get; set; } = 24;

    /// <summary>
    /// How often <see cref="UserPurgeService"/> wakes to check whether a sweep is due. Deliberately
    /// decoupled from <see cref="IntervalHours"/> so a transient failure costs one tick, not one interval.
    /// </summary>
    public int TickIntervalMinutes { get; set; } = 60;

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
    /// <summary>
    /// Consecutive <see cref="IUserRepository.DeleteAsync"/> failures that abort the rest of a sweep.
    /// </summary>
    internal const int MaxConsecutiveDeleteFailures = 3;

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

        var max = Math.Max(1, options.MaxDeletesPerRun);
        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, options.PurgeAfterDays));

        var candidates = await _userRepository.GetPurgeCandidatesAsync(cutoff, max).ConfigureAwait(false);
        result.CandidatesFound = candidates.Count;

        var consecutiveDeleteFailures = 0;

        foreach (var user in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Deletes are audited before they are attempted (see below), so a systematically failing
            // delete would otherwise write two entries per candidate for the whole batch on every sweep -
            // hundreds of audit rows describing purges that never happened. Stop early instead: if several
            // deletes in a row fail, the container is unhealthy and the remaining candidates will fail too.
            if (consecutiveDeleteFailures >= MaxConsecutiveDeleteFailures)
            {
                _logger.LogError(
                    "Aborting the purge sweep after {Count} consecutive delete failures; {Remaining} "
                        + "candidates were not attempted",
                    consecutiveDeleteFailures,
                    result.CandidatesFound
                        - result.Deleted
                        - result.WouldDelete
                        - result.SkippedAdministrator
                        - result.Errors
                );
                break;
            }

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

            // Audit first, so a deletion can never go unrecorded: the delete is irreversible and the entry
            // is the only durable trace of who was removed, whereas a failure here simply leaves the user
            // in place for the next sweep. A failed audit write must therefore abort this candidate.
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
                result.Errors++;
                _logger.LogError(ex, "Failed to audit the purge of user {UniqueId}; not deleting", user.UniqueId);
                continue;
            }

            try
            {
                await _userRepository.DeleteAsync(user.UniqueId).ConfigureAwait(false);
                result.Deleted++;
                consecutiveDeleteFailures = 0;
                _logger.LogInformation("Purged user {UniqueId}", user.UniqueId);
            }
            catch (Exception ex)
            {
                result.Errors++;
                consecutiveDeleteFailures++;
                _logger.LogError(ex, "Failed to purge user {UniqueId}", user.UniqueId);

                // The audit entry above asserts a completed deletion that may not have happened. Record the
                // uncertainty rather than either leaving the claim unqualified or denying it: a throw does
                // not prove the delete did not commit (a write timeout can surface after the server
                // applied it), so an entry stating it definitely failed could itself be false and would
                // never be corrected, because a deleted user is never a candidate again. Best-effort - if
                // this write also fails, the error logged above is all that is left.
                try
                {
                    await _auditLogRepository
                        .AddAsync(
                            new NewAuditLogEntry
                            {
                                Id = Guid.NewGuid(),
                                SubjectId = user.UniqueId,
                                SubjectName = user.Emails,
                                Action =
                                    "Purge of inactive user did not complete cleanly; the deletion may or "
                                    + "may not have been applied. Verify whether the preceding purge entry "
                                    + "for this user reflects a completed deletion.",
                                Time = DateTime.UtcNow,
                                UserDisplayName = "System",
                                UserId = "user-purge",
                            }
                        )
                        .ConfigureAwait(false);
                }
                catch (Exception compensationEx)
                {
                    _logger.LogError(
                        compensationEx,
                        "Failed to write the compensating audit entry for user {UniqueId}; the audit log "
                            + "now records a purge that did not complete",
                        user.UniqueId
                    );
                }
            }
        }

        return result;
    }
}
