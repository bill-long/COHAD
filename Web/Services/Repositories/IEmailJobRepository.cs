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
    }
}
