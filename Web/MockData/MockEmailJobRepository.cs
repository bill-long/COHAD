#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Web.Models;
using Web.Services.Repositories;

namespace Web.MockData
{
    public sealed class MockEmailJobRepository : IEmailJobRepository
    {
        private readonly Dictionary<Guid, EmailJob> _jobs = new();
        private int _versionCounter;

        public Task AddAsync(EmailJob job)
        {
            lock (_jobs)
            {
                var clone = CloneJob(job);
                clone.ETag = Interlocked.Increment(ref _versionCounter).ToString();
                _jobs[job.Id] = clone;
                job.ETag = clone.ETag;
            }

            return Task.CompletedTask;
        }

        public Task<EmailJob?> GetByIdAsync(Guid jobId)
        {
            lock (_jobs)
            {
                return Task.FromResult(_jobs.TryGetValue(jobId, out var found) ? CloneJob(found) : null);
            }
        }

        public Task DeleteAsync(Guid jobId)
        {
            lock (_jobs)
            {
                _jobs.Remove(jobId);
            }

            return Task.CompletedTask;
        }

        public Task UpdateAsync(EmailJob job)
        {
            lock (_jobs)
            {
                if (!_jobs.TryGetValue(job.Id, out var stored))
                    throw new InvalidOperationException($"Email job {job.Id} does not exist.");

                if (!string.IsNullOrEmpty(job.ETag) && stored.ETag != job.ETag)
                    throw new EmailJobConcurrencyException();

                var clone = CloneJob(job);
                clone.ETag = Interlocked.Increment(ref _versionCounter).ToString();
                _jobs[job.Id] = clone;
                job.ETag = clone.ETag;
            }

            return Task.CompletedTask;
        }

        public Task<bool> TryClaimAsync(EmailJob job)
        {
            lock (_jobs)
            {
                if (!_jobs.TryGetValue(job.Id, out var stored))
                    return Task.FromResult(false);

                if (!string.IsNullOrEmpty(job.ETag) && stored.ETag != job.ETag)
                    return Task.FromResult(false);

                var clone = CloneJob(job);
                clone.ETag = Interlocked.Increment(ref _versionCounter).ToString();
                _jobs[job.Id] = clone;
                job.ETag = clone.ETag;
                return Task.FromResult(true);
            }
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
                var effectiveLimit = Math.Clamp(limit, 1, 100);
                var list = _jobs.Values
                    .OrderByDescending(j => j.CreatedUtc)
                    .Take(effectiveLimit)
                    .Select(CloneJob)
                    .ToList();
                return Task.FromResult(list);
            }
        }

        private static EmailJob CloneJob(EmailJob job)
        {
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
                LastProgressUtc = job.LastProgressUtc,
                CreatedByUserId = job.CreatedByUserId,
                CreatedByDisplayName = job.CreatedByDisplayName,
                MaxRecipientAttempts = job.MaxRecipientAttempts,
                TotalRecipients = job.TotalRecipients,
                SentCount = job.SentCount,
                FailedCount = job.FailedCount,
                LastError = job.LastError,
                ETag = job.ETag,
                Recipients = job.Recipients?.Select(r => new EmailJobRecipient
                {
                    Email = r.Email,
                    HomeId = r.HomeId,
                    Status = r.Status,
                    AttemptCount = r.AttemptCount,
                    LastAttemptUtc = r.LastAttemptUtc,
                    Error = r.Error,
                    SentUtc = r.SentUtc
                }).ToList() ?? new List<EmailJobRecipient>()
            };
        }
    }
}
