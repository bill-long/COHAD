#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Web.Models;
using Web.Services.Repositories;

namespace Web.MockData
{
    public sealed class MockEmailJobRepository : IEmailJobRepository
    {
        private readonly Dictionary<Guid, EmailJob> _jobs = new();

        public Task AddAsync(EmailJob job)
        {
            lock (_jobs)
            {
                _jobs[job.Id] = CloneJob(job);
            }

            return Task.CompletedTask;
        }

        public Task<EmailJob> GetByIdAsync(Guid jobId)
        {
            lock (_jobs)
            {
                return Task.FromResult(_jobs.TryGetValue(jobId, out var found) ? CloneJob(found) : null);
            }
        }

        public Task UpdateAsync(EmailJob job)
        {
            lock (_jobs)
            {
                _jobs[job.Id] = CloneJob(job);
            }

            return Task.CompletedTask;
        }

        public Task<List<EmailJob>> GetIncompleteJobsAsync()
        {
            lock (_jobs)
            {
                var list = _jobs.Values
                    .Where(j => j.Status == EmailJobStatus.Queued || j.Status == EmailJobStatus.InProgress)
                    .OrderBy(j => j.CreatedUtc)
                    .Select(CloneJob)
                    .ToList();
                return Task.FromResult(list);
            }
        }

        public Task<List<EmailJob>> GetRecentJobsAsync(int limit)
        {
            lock (_jobs)
            {
                var list = _jobs.Values
                    .OrderByDescending(j => j.CreatedUtc)
                    .Take(limit)
                    .Select(CloneJob)
                    .ToList();
                return Task.FromResult(list);
            }
        }

        private static EmailJob CloneJob(EmailJob job)
        {
            if (job == null)
            {
                return null;
            }

            return new EmailJob
            {
                Id = job.Id,
                Status = job.Status,
                Category = job.Category,
                FromEmail = job.FromEmail,
                FromDisplay = job.FromDisplay,
                Subject = job.Subject,
                ContentBlobPath = job.ContentBlobPath,
                CreatedUtc = job.CreatedUtc,
                StartedUtc = job.StartedUtc,
                CompletedUtc = job.CompletedUtc,
                CreatedByUserId = job.CreatedByUserId,
                CreatedByDisplayName = job.CreatedByDisplayName,
                TotalRecipients = job.TotalRecipients,
                SentCount = job.SentCount,
                FailedCount = job.FailedCount,
                LastError = job.LastError,
                Recipients = job.Recipients?.Select(r => new EmailJobRecipient
                {
                    Email = r.Email,
                    HomeId = r.HomeId,
                    Status = r.Status,
                    Error = r.Error,
                    SentUtc = r.SentUtc
                }).ToList() ?? new List<EmailJobRecipient>()
            };
        }
    }
}
