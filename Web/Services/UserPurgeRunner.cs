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

    public bool DryRun { get; set; }

    public int PurgeAfterDays { get; set; } = 30;

    public int MaxDeletesPerRun { get; set; } = 100;
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
        ILogger<UserPurgeRunner> logger)
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
                await _auditLogRepository.AddAsync(new NewAuditLogEntry
                {
                    Id = Guid.NewGuid(),
                    SubjectId = user.UniqueId,
                    SubjectName = user.Emails,
                    Action = $"Purged inactive user (no homes or no roles for {options.PurgeAfterDays}+ days).",
                    Time = DateTime.UtcNow,
                    UserDisplayName = "System",
                    UserId = "user-purge"
                }).ConfigureAwait(false);

                await _userRepository.DeleteAsync(user.UniqueId).ConfigureAwait(false);
                result.Deleted++;
                _logger.LogInformation("Purged user {UniqueId}", user.UniqueId);
            }
            catch (Exception ex)
            {
                result.Errors++;
                _logger.LogError(ex, "Failed to purge user {UniqueId}", user.UniqueId);
            }
        }

        return result;
    }
}
