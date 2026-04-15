#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Web.Models;
using Web.Services;
using Web.Services.Repositories;

namespace Web.MockData
{
    public sealed class MockEmailJobRepository : IEmailJobRepository
    {
        private const string SeedHtmlBody = "<p>Mock seed email body (for local testing).</p>";

        private readonly Dictionary<Guid, EmailJob> _jobs = new();
        private int _versionCounter;

        public MockEmailJobRepository(IDocumentFileStore fileStore)
        {
            SeedSampleJobs(fileStore);
        }

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
                var list = _jobs
                    .Values.Where(j => j.Status == EmailJobStatus.Queued || j.Status == EmailJobStatus.InProgress)
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
                var list = _jobs
                    .Values.OrderByDescending(j => j.CreatedUtc)
                    .Take(effectiveLimit)
                    .Select(CloneJob)
                    .ToList();
                return Task.FromResult(list);
            }
        }

        public Task<List<EmailJob>> GetTerminalJobsOlderThanAsync(DateTime cutoffUtc, int limit)
        {
            lock (_jobs)
            {
                var effectiveLimit = Math.Clamp(limit, 1, 250);
                var list = _jobs
                    .Values.Where(j =>
                        j.CreatedUtc < cutoffUtc
                        && j.Status != EmailJobStatus.Queued
                        && j.Status != EmailJobStatus.InProgress
                    )
                    .OrderBy(j => j.CreatedUtc)
                    .Take(effectiveLimit)
                    .Select(CloneJob)
                    .ToList();
                return Task.FromResult(list);
            }
        }

        public Task<List<EmailJob>> GetRecentlyCompletedJobsAsync(DateTime completedAfterUtc, int limit)
        {
            lock (_jobs)
            {
                var effectiveLimit = Math.Clamp(limit, 1, 250);
                var list = _jobs
                    .Values.Where(j =>
                        j.CompletedUtc >= completedAfterUtc
                        && j.Status != EmailJobStatus.Queued
                        && j.Status != EmailJobStatus.InProgress
                        && j.Status != EmailJobStatus.Cancelled
                    )
                    .OrderBy(j => j.CompletedUtc)
                    .Take(effectiveLimit)
                    .Select(CloneJob)
                    .ToList();
                return Task.FromResult(list);
            }
        }

        public Task<EmailJob?> GetByInternetMessageIdAsync(string internetMessageId, string fromEmail)
        {
            if (string.IsNullOrEmpty(internetMessageId))
                return Task.FromResult<EmailJob?>(null);

            lock (_jobs)
            {
                var match = _jobs.Values.FirstOrDefault(j =>
                    j.InternetMessageId == internetMessageId
                    && string.Equals(j.FromEmail, fromEmail, StringComparison.OrdinalIgnoreCase)
                );
                return Task.FromResult(match != null ? CloneJob(match) : (EmailJob?)null);
            }
        }

        /// <summary>
        /// Pre-populates completed jobs so Manage → Email has a tall list without sending repeatedly.
        /// </summary>
        private void SeedSampleJobs(IDocumentFileStore fileStore)
        {
            var now = DateTime.UtcNow;
            var adminId = MockDataConstants.AdminUniqueId;
            const string displayName = "Mock Admin";

            var definitions = new (Guid Id, string Subject, int DaysAgo, int Sent, int Failed)[]
            {
                (MockDataConstants.SampleEmailJobSeed1, "[Mock] Annual HOA assessment reminder", 14, 2, 0),
                (MockDataConstants.SampleEmailJobSeed2, "[Mock] Pool closure — maintenance week", 12, 2, 0),
                (MockDataConstants.SampleEmailJobSeed3, "[Mock] Neighborhood garage sale", 10, 1, 1),
                (MockDataConstants.SampleEmailJobSeed4, "[Mock] Board meeting minutes (March)", 8, 2, 0),
                (MockDataConstants.SampleEmailJobSeed5, "[Mock] Garden club spring planting day", 6, 2, 0),
                (MockDataConstants.SampleEmailJobSeed6, "[Mock] Trash pickup schedule change", 4, 2, 0),
                (MockDataConstants.SampleEmailJobSeed7, "[Mock] Welcome new residents", 2, 2, 0),
                (MockDataConstants.SampleEmailJobSeed8, "[Mock] Holiday lights contest", 1, 2, 0),
            };

            lock (_jobs)
            {
                foreach (var d in definitions)
                {
                    var contentPath = $"email-jobs/{d.Id:D}.html";
                    var created = now.AddDays(-d.DaysAgo).AddHours(-2);
                    var started = created.AddMinutes(2);
                    var completed = started.AddMinutes(5);

                    var recipients = BuildSeedRecipients(d.Sent, d.Failed, completed);

                    var job = new EmailJob
                    {
                        Id = d.Id,
                        Status =
                            d.Failed > 0 && d.Sent == 0 ? EmailJobStatus.Failed
                            : d.Failed > 0 ? EmailJobStatus.PartiallyCompleted
                            : EmailJobStatus.Completed,
                        Category = "board",
                        FromEmail = "board@cohad.org",
                        FromDisplay = "COHAD Board",
                        Subject = d.Subject,
                        ContentBlobPath = contentPath,
                        CreatedUtc = created,
                        StartedUtc = started,
                        CompletedUtc = completed,
                        LastProgressUtc = completed,
                        CreatedByUserId = adminId,
                        CreatedByDisplayName = displayName,
                        MaxRecipientAttempts = 3,
                        TotalRecipients = recipients.Count,
                        SentCount = d.Sent,
                        FailedCount = d.Failed,
                        LastError = null,
                        Recipients = recipients,
                        ETag = Interlocked.Increment(ref _versionCounter).ToString(),
                    };

                    _jobs[job.Id] = CloneJob(job);
                }
            }

            var htmlBytes = Encoding.UTF8.GetBytes(SeedHtmlBody);
            foreach (var d in definitions)
            {
                var path = $"email-jobs/{d.Id:D}.html";
                using var stream = new MemoryStream(htmlBytes, writable: false);
                fileStore.UploadAsync(path, stream, "text/html").GetAwaiter().GetResult();
            }
        }

        private static List<EmailJobRecipient> BuildSeedRecipients(int sent, int failed, DateTime completedUtc)
        {
            var list = new List<EmailJobRecipient>();
            var emails = new[]
            {
                ("mock@cohad.local", MockDataConstants.SampleHomeId),
                ("taylor@cohad.local", MockDataConstants.SecondSampleHomeId),
            };

            int idx = 0;
            for (var i = 0; i < sent && idx < emails.Length; i++, idx++)
            {
                var (email, homeId) = emails[idx];
                list.Add(
                    new EmailJobRecipient
                    {
                        Email = email,
                        HomeId = homeId,
                        Status = EmailJobRecipientStatus.Sent,
                        AttemptCount = 1,
                        LastAttemptUtc = completedUtc.AddSeconds(-30 - i * 5),
                        SentUtc = completedUtc.AddSeconds(-25 - i * 5),
                        Error = null,
                    }
                );
            }

            for (var i = 0; i < failed && idx < emails.Length; i++, idx++)
            {
                var (email, homeId) = emails[idx];
                list.Add(
                    new EmailJobRecipient
                    {
                        Email = email,
                        HomeId = homeId,
                        Status = EmailJobRecipientStatus.Failed,
                        AttemptCount = 1,
                        LastAttemptUtc = completedUtc.AddSeconds(-20),
                        SentUtc = null,
                        Error = "Mock send failure (seed data).",
                    }
                );
            }

            return list;
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
                GroupRecipients = job.GroupRecipients,
                InternetMessageId = job.InternetMessageId,
                ReplyToEmail = job.ReplyToEmail,
                ReplyToDisplay = job.ReplyToDisplay,
                ETag = job.ETag,
                Attachments =
                    job.Attachments?.Select(a => new EmailJobAttachment
                        {
                            FileName = a.FileName,
                            BlobPath = a.BlobPath,
                            ContentType = a.ContentType,
                            Size = a.Size,
                        })
                        .ToList()
                    ?? new(),
                Recipients =
                    job.Recipients?.Select(r => new EmailJobRecipient
                        {
                            Email = r.Email,
                            HomeId = r.HomeId,
                            Status = r.Status,
                            AttemptCount = r.AttemptCount,
                            LastAttemptUtc = r.LastAttemptUtc,
                            Error = r.Error,
                            SentUtc = r.SentUtc,
                        })
                        .ToList()
                    ?? new List<EmailJobRecipient>(),
            };
        }
    }
}
