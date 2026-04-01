using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Web.Models;

namespace Web.Services.Repositories
{
    public interface IEmailJobRepository
    {
        Task AddAsync(EmailJob job);

        Task<EmailJob> GetByIdAsync(Guid jobId);

        Task UpdateAsync(EmailJob job);

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
