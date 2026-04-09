#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Web.Models;

namespace Web.Services.Repositories
{
    public interface IEmailJobRepository
    {
        Task AddAsync(EmailJob job);

        Task<EmailJob?> GetByIdAsync(Guid jobId);

        /// <summary>
        /// Removes the job document. Used for compensating cleanup; ignores missing documents.
        /// </summary>
        Task DeleteAsync(Guid jobId);

        Task UpdateAsync(EmailJob job);

        /// <summary>
        /// Atomically claims a job by transitioning it to InProgress only if its ETag
        /// matches the expected value (i.e., no other instance has modified it since it was read).
        /// Returns true if the claim succeeded; false if another instance claimed it first.
        /// </summary>
        Task<bool> TryClaimAsync(EmailJob job);

        /// <summary>
        /// Returns jobs with status Queued or InProgress, ordered by CreatedUtc ascending.
        /// Used on startup to resume incomplete jobs.
        /// </summary>
        Task<List<EmailJob>> GetIncompleteJobsAsync();

        /// <summary>
        /// Returns the most recent jobs (any status), ordered by CreatedUtc descending.
        /// </summary>
        Task<List<EmailJob>> GetRecentJobsAsync(int limit);

        /// <summary>
        /// Returns terminal jobs older than the given cutoff (CreatedUtc &lt; cutoffUtc), ordered by CreatedUtc ascending.
        /// Used for retention cleanup.
        /// </summary>
        Task<List<EmailJob>> GetTerminalJobsOlderThanAsync(DateTime cutoffUtc, int limit);

        /// <summary>
        /// Returns the first job with the given <see cref="EmailJob.GraphMessageId"/>, or null.
        /// Used by <see cref="CommitteeMailPoller"/> as an idempotency check to avoid creating
        /// duplicate forwarding jobs for the same mailbox message.
        /// </summary>
        Task<EmailJob?> GetByGraphMessageIdAsync(string graphMessageId);
    }
}
