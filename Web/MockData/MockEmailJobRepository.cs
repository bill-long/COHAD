#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
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
                if (_jobs.ContainsKey(job.Id))
                {
                    // Mirror Cosmos CreateItemAsync, which fails with 409 on a duplicate id. The mail
                    // poller assigns deterministic ids and relies on this to dedup concurrent adds.
                    throw new CosmosException("Email job already exists.", HttpStatusCode.Conflict, 0, string.Empty, 0);
                }

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
                var effectiveLimit = Math.Clamp(limit, 1, IEmailJobRepository.MaxRecentlyCompletedJobsLimit);
                var list = _jobs
                    .Values.Where(j =>
                        j.CompletedUtc >= completedAfterUtc
                        && j.Status != EmailJobStatus.Queued
                        && j.Status != EmailJobStatus.InProgress
                        && j.Status != EmailJobStatus.Cancelled
                    )
                    .OrderByDescending(j => j.CompletedUtc)
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

            // Seed3 (10 days ago) is the job whose bounce created the seeded suppression of
            // taylor.old@cohad.local (see MockEmailSuppressionRepository.SeedSampleData, whose
            // CausingJobId points at it); Seed5 onward post-date the suppression, and Seed5
            // carries the resulting Suppressed recipient so the job detail page's in-place
            // explanation is exercisable. Earlier jobs pre-date the address's failure and
            // deliberately do not mention it.
            var definitions = new (Guid Id, string Subject, int DaysAgo, int Sent, int Failed, int Suppressed)[]
            {
                (MockDataConstants.SampleEmailJobSeed1, "[Mock] Annual HOA assessment reminder", 14, 2, 0, 0),
                (MockDataConstants.SampleEmailJobSeed2, "[Mock] Pool closure — maintenance week", 12, 2, 0, 0),
                (MockDataConstants.SampleEmailJobSeed3, "[Mock] Neighborhood garage sale", 10, 1, 1, 0),
                (MockDataConstants.SampleEmailJobSeed4, "[Mock] Board meeting minutes (March)", 8, 2, 0, 0),
                (MockDataConstants.SampleEmailJobSeed5, "[Mock] Garden club spring planting day", 6, 2, 0, 1),
                (MockDataConstants.SampleEmailJobSeed6, "[Mock] Trash pickup schedule change", 4, 2, 0, 0),
                (MockDataConstants.SampleEmailJobSeed7, "[Mock] Welcome new residents", 2, 2, 0, 0),
                (MockDataConstants.SampleEmailJobSeed8, "[Mock] Holiday lights contest", 1, 2, 0, 0),
            };

            lock (_jobs)
            {
                foreach (var d in definitions)
                {
                    var contentPath = $"email-jobs/{d.Id:D}.html";
                    var created = now.AddDays(-d.DaysAgo).AddHours(-2);
                    var started = created.AddMinutes(2);
                    var completed = started.AddMinutes(5);

                    var recipients = BuildSeedRecipients(d.Sent, d.Failed, d.Suppressed, completed);

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
                        // Built the same way SendCommitteeEmail builds it, so mock mode cannot show a
                        // description production never renders.
                        ToDisplay = EmailAudience.ForCommitteeSend("Board"),
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
                        SuppressedCount = d.Suppressed,
                        LastError = null,
                        Recipients = recipients,
                        ETag = Interlocked.Increment(ref _versionCounter).ToString(),
                    };

                    _jobs[job.Id] = CloneJob(job);
                }

                _jobs[MockDataConstants.SampleForwardedEmailJobSeed] = CloneJob(BuildSeedForwardedJob(now));
            }

            var htmlBytes = Encoding.UTF8.GetBytes(SeedHtmlBody);
            foreach (var path in definitions
                .Select(d => $"email-jobs/{d.Id:D}.html")
                .Append($"email-jobs/{MockDataConstants.SampleForwardedEmailJobSeed:D}.html"))
            {
                using var stream = new MemoryStream(htmlBytes, writable: false);
                fileStore.UploadAsync(path, stream, "text/html").GetAwaiter().GetResult();
            }
        }

        /// <summary>
        /// A committee-forwarding job: written by a resident, sent as the committee mailbox, delivered
        /// to the committee's forwarding members. Its From (the mailbox) and its author are deliberately
        /// different so the job pages can be checked against the case they were confusing.
        /// <para>
        /// The mailbox and recipients must match the Architectural Committee seeded by
        /// <c>MockCommitteeRepository</c> - one forwarding member, the chair at mock@cohad.local - or
        /// cross-checking this job against Manage - Committees shows a combination the real forwarding
        /// paths cannot produce.
        /// </para>
        /// </summary>
        private EmailJob BuildSeedForwardedJob(DateTime now)
        {
            var created = now.AddDays(-3);
            var completed = created.AddMinutes(3);

            var job = new EmailJob
            {
                Id = MockDataConstants.SampleForwardedEmailJobSeed,
                Status = EmailJobStatus.Completed,
                Category = EmailJob.CommitteeForwardCategory,
                FromEmail = MockDataConstants.ArchitecturalCommitteeEmail,
                FromDisplay = MockDataConstants.ArchitecturalCommitteeDisplayName,
                Subject = "Fwd: [Mock] Request to repaint front door",
                ContentBlobPath = $"email-jobs/{MockDataConstants.SampleForwardedEmailJobSeed:D}.html",
                CreatedUtc = created,
                StartedUtc = created.AddMinutes(1),
                CompletedUtc = completed,
                LastProgressUtc = completed,
                CreatedByUserId = "system:mail-poller",
                CreatedByDisplayName = "Committee Mail Poller",
                MaxRecipientAttempts = 3,
                GroupRecipients = true,
                InternetMessageId = "<mock-forward-seed@cohad.local>",
                // The committee's single forwarding member. Taylor is the author of this message and
                // is not on the committee, so a forward would never be delivered back to them.
                Recipients = new List<EmailJobRecipient>
                {
                    new()
                    {
                        Email = "mock@cohad.local",
                        HomeId = MockDataConstants.SampleHomeId,
                        Status = EmailJobRecipientStatus.Sent,
                        AttemptCount = 1,
                        LastAttemptUtc = completed.AddSeconds(-30),
                        SentUtc = completed.AddSeconds(-25),
                    },
                },
                ETag = Interlocked.Increment(ref _versionCounter).ToString(),
            };

            job.TotalRecipients = job.Recipients.Count;
            job.SentCount = job.Recipients.Count;

            CommitteeForwardJob.ApplyOriginator(job, job.FromDisplay, "taylor@cohad.local", "Taylor Test");

            return job;
        }

        private static List<EmailJobRecipient> BuildSeedRecipients(
            int sent,
            int failed,
            int suppressed,
            DateTime completedUtc
        )
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

            for (var i = 0; i < suppressed; i++)
            {
                // The shape the enforcement point produces: no attempt consumed, no error text,
                // the when and why stamped on the recipient (see EmailJobProcessor.ApplySuppressions).
                list.Add(
                    new EmailJobRecipient
                    {
                        Email = "taylor.old@cohad.local",
                        HomeId = MockDataConstants.SecondSampleHomeId,
                        Status = EmailJobRecipientStatus.Suppressed,
                        AttemptCount = 0,
                        SuppressedUtc = completedUtc.AddSeconds(-35),
                        SuppressionReason = SuppressionReason.HardBounce,
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
                ToDisplay = job.ToDisplay,
                OriginalSenderEmail = job.OriginalSenderEmail,
                OriginalSenderDisplay = job.OriginalSenderDisplay,
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
                SuppressedCount = job.SuppressedCount,
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
                            // Every field, matching what the Cosmos mapper round-trips. This clone
                            // previously dropped the delivery-event fields and UnsubscribeLinkId,
                            // which mock mode happened never to read back - the suppression stamps
                            // ARE read back (the job detail page renders them), so the gap stopped
                            // being latent and the whole set is aligned rather than just the new
                            // two.
                            DeliveryStatus = r.DeliveryStatus,
                            DeliveryStatusUpdatedUtc = r.DeliveryStatusUpdatedUtc,
                            ProviderMessageId = r.ProviderMessageId,
                            Provider = r.Provider,
                            UnsubscribeLinkId = r.UnsubscribeLinkId,
                            SuppressedUtc = r.SuppressedUtc,
                            SuppressionReason = r.SuppressionReason,
                        })
                        .ToList()
                    ?? new List<EmailJobRecipient>(),
            };
        }
    }
}
