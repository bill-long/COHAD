using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Web.Services.Repositories;

namespace Web.Services;

public sealed class EmailJobCleanupService
{
    private readonly IEmailJobRepository _repo;
    private readonly IDocumentFileStore _fileStore;
    private readonly ILogger<EmailJobCleanupService> _logger;
    private readonly IConfiguration _config;

    public EmailJobCleanupService(
        IEmailJobRepository repo,
        IDocumentFileStore fileStore,
        IConfiguration config,
        ILogger<EmailJobCleanupService> logger)
    {
        _repo = repo;
        _fileStore = fileStore;
        _config = config;
        _logger = logger;
    }

    public async Task RunOnceBestEffortAsync()
    {
        var retentionDays = Math.Clamp(_config.GetValue("EmailJobs:RetentionDays", 90), 1, 3650);
        var batchSize = Math.Clamp(_config.GetValue("EmailJobs:CleanupBatchSize", 25), 1, 250);

        var cutoffUtc = DateTime.UtcNow.AddDays(-retentionDays);
        var jobs = await _repo.GetTerminalJobsOlderThanAsync(cutoffUtc, batchSize);

        if (jobs.Count == 0)
            return;

        foreach (var job in jobs)
        {
            if (!string.IsNullOrWhiteSpace(job.ContentBlobPath))
            {
                try
                {
                    await _fileStore.DeleteAsync(job.ContentBlobPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Email job retention: failed to delete blob {BlobPath} for job {JobId}",
                        job.ContentBlobPath, job.Id);
                }
            }

            try
            {
                await _repo.DeleteAsync(job.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Email job retention: failed to delete job {JobId}", job.Id);
            }
        }

        _logger.LogInformation(
            "Email job retention cleanup deleted {Count} jobs older than {RetentionDays} days",
            jobs.Count, retentionDays);
    }
}

