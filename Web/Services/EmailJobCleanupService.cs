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
        ILogger<EmailJobCleanupService> logger
    )
    {
        _repo = repo;
        _fileStore = fileStore;
        _config = config;
        _logger = logger;
    }

    public async Task RunOnceBestEffortAsync()
    {
        try
        {
            var retentionDays = Math.Clamp(_config.GetValue("EmailJobs:RetentionDays", 90), 1, 3650);
            var batchSize = Math.Clamp(_config.GetValue("EmailJobs:CleanupBatchSize", 25), 1, 250);

            var cutoffUtc = DateTime.UtcNow.AddDays(-retentionDays);
            var jobs = await _repo.GetTerminalJobsOlderThanAsync(cutoffUtc, batchSize);

            if (jobs.Count == 0)
                return;

            var attemptedJobs = 0;
            var deletedJobs = 0;
            var attemptedBlobs = 0;
            var deletedBlobs = 0;

            foreach (var job in jobs)
            {
                attemptedJobs++;

                var blobDeletedOrAbsent = true;
                if (!string.IsNullOrWhiteSpace(job.ContentBlobPath))
                {
                    attemptedBlobs++;
                    try
                    {
                        await _fileStore.DeleteAsync(job.ContentBlobPath);
                        deletedBlobs++;
                    }
                    catch (Exception ex)
                    {
                        blobDeletedOrAbsent = false;
                        _logger.LogWarning(
                            ex,
                            "Email job retention: failed to delete blob {BlobPath} for job {JobId}",
                            job.ContentBlobPath,
                            job.Id
                        );
                    }
                }

                if (!blobDeletedOrAbsent)
                {
                    // Do not delete the job row if the blob couldn't be removed; otherwise we'd orphan the blob
                    // and lose the path needed to retry deletion on a later cleanup pass.
                    continue;
                }

                try
                {
                    await _repo.DeleteAsync(job.Id);
                    deletedJobs++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Email job retention: failed to delete job {JobId}", job.Id);
                }
            }

            _logger.LogInformation(
                "Email job retention cleanup attempted {AttemptedJobs} job deletions and deleted {DeletedJobs}; attempted {AttemptedBlobs} blob deletes and deleted {DeletedBlobs} (cutoff {CutoffUtc:u})",
                attemptedJobs,
                deletedJobs,
                attemptedBlobs,
                deletedBlobs,
                cutoffUtc
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Email job retention: cleanup run failed unexpectedly.");
        }
    }
}
