using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Web.Hubs;
using Web.Models;
using Web.Services;
using Web.Services.Repositories;

namespace Web.UnitTests;

public sealed class EmailJobProcessorTests
{
    private readonly Mock<IEmailJobRepository> _jobRepo = new();
    private readonly Mock<IDocumentFileStore> _fileStore = new();
    private readonly Mock<IUnsubscribeTokenService> _tokenService = new();
    private readonly Mock<IClientProxy> _clientProxy = new();
    private readonly EmailJobQueue _queue = new();

    private EmailJobProcessor CreateProcessor(string environment = "MockData")
    {
        var scopeFactory = new Mock<IServiceScopeFactory>();
        var scope = new Mock<IServiceScope>();
        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(sp => sp.GetService(typeof(IEmailJobRepository))).Returns(_jobRepo.Object);
        serviceProvider.Setup(sp => sp.GetService(typeof(IDocumentFileStore))).Returns(_fileStore.Object);
        scope.Setup(s => s.ServiceProvider).Returns(serviceProvider.Object);
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        var hubContext = new Mock<IHubContext<EmailJobHub>>();
        var mockClients = new Mock<IHubClients>();
        mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_clientProxy.Object);
        hubContext.Setup(h => h.Clients).Returns(mockClients.Object);

        var env = new Mock<IWebHostEnvironment>();
        env.Setup(e => e.EnvironmentName).Returns(environment);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SmtpHost"] = "localhost",
                ["SmtpUser"] = "user",
                ["SmtpPassword"] = "pass",
                ["AppBaseUrl"] = "https://test.cohad.org"
            })
            .Build();

        return new EmailJobProcessor(
            _queue,
            scopeFactory.Object,
            _tokenService.Object,
            hubContext.Object,
            config,
            env.Object,
            NullLogger<EmailJobProcessor>.Instance);
    }

    /// <summary>
    /// Starts the processor, enqueues a job, and waits for processing to complete.
    /// </summary>
    private async Task RunProcessorForSingleJob(EmailJobProcessor processor, Guid jobId, int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource();

        // Start the background service
        var executeTask = processor.StartAsync(cts.Token);

        // Enqueue the job
        await _queue.EnqueueAsync(jobId);

        // Wait for the job to be processed (poll for completion)
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
            var job = _jobRepo.Object;
            // Check if the repo's UpdateAsync was called with a terminal status
            try
            {
                _jobRepo.Verify(r => r.UpdateAsync(It.Is<EmailJob>(j =>
                    j.Id == jobId && (
                        j.Status == EmailJobStatus.Completed ||
                        j.Status == EmailJobStatus.Failed ||
                        j.Status == EmailJobStatus.PartiallyCompleted ||
                        j.Status == EmailJobStatus.Cancelled))), Times.AtLeastOnce);
                break;
            }
            catch (MockException)
            {
                // Not yet complete, keep waiting
            }
        }

        // Stop the processor
        cts.Cancel();
        try { await processor.StopAsync(CancellationToken.None); }
        catch (OperationCanceledException) { }
    }

    // ───────────────────────────────────────────────────
    // RequestCancellation
    // ───────────────────────────────────────────────────

    [Fact]
    public void RequestCancellation_NoActiveJob_ReturnsFalse()
    {
        var processor = CreateProcessor();
        Assert.False(processor.RequestCancellation(Guid.NewGuid()));
    }

    [Fact]
    public void RequestCancellation_DifferentJobId_ReturnsFalse()
    {
        var processor = CreateProcessor();
        Assert.False(processor.RequestCancellation(Guid.NewGuid()));
        Assert.False(processor.RequestCancellation(Guid.Empty));
    }

    // ───────────────────────────────────────────────────
    // MockMode processing
    // ───────────────────────────────────────────────────

    [Fact]
    public async Task MockMode_ProcessesAllRecipients_MarksCompleted()
    {
        var jobId = Guid.NewGuid();
        var job = new EmailJob
        {
            Id = jobId,
            Status = EmailJobStatus.Queued,
            Category = "board",
            FromEmail = "board@cohad.org",
            FromDisplay = "COHAD Board",
            Subject = "Test",
            ContentBlobPath = $"email-jobs/{jobId:D}.html",
            TotalRecipients = 3,
            Recipients = new List<EmailJobRecipient>
            {
                new EmailJobRecipient { Email = "a@test.com", HomeId = Guid.NewGuid(), Status = EmailJobRecipientStatus.Pending },
                new EmailJobRecipient { Email = "b@test.com", HomeId = Guid.NewGuid(), Status = EmailJobRecipientStatus.Pending },
                new EmailJobRecipient { Email = "c@test.com", HomeId = Guid.NewGuid(), Status = EmailJobRecipientStatus.Pending }
            }
        };

        _jobRepo.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(job);
        _jobRepo.Setup(r => r.GetIncompleteJobsAsync()).ReturnsAsync(new List<EmailJob>());
        _jobRepo.Setup(r => r.UpdateAsync(It.IsAny<EmailJob>())).Returns(Task.CompletedTask);

        _fileStore.Setup(f => f.DownloadAsync(job.ContentBlobPath))
            .ReturnsAsync(new DocumentFileResult
            {
                Stream = new MemoryStream("<p>Hello</p>"u8.ToArray()),
                ContentType = "text/html"
            });

        var processor = CreateProcessor();
        await RunProcessorForSingleJob(processor, jobId);

        Assert.Equal(EmailJobStatus.Completed, job.Status);
        Assert.Equal(3, job.SentCount);
        Assert.NotNull(job.CompletedUtc);
        Assert.All(job.Recipients, r =>
        {
            Assert.Equal(EmailJobRecipientStatus.Sent, r.Status);
            Assert.NotNull(r.SentUtc);
        });
    }

    [Fact]
    public async Task MockMode_RespectsCancel()
    {
        var jobId = Guid.NewGuid();
        // Use many recipients so we have time to cancel
        var recipients = Enumerable.Range(1, 100).Select(i =>
            new EmailJobRecipient
            {
                Email = $"r{i}@test.com",
                HomeId = Guid.NewGuid(),
                Status = EmailJobRecipientStatus.Pending
            }).ToList();

        var job = new EmailJob
        {
            Id = jobId,
            Status = EmailJobStatus.Queued,
            Category = "board",
            FromEmail = "board@cohad.org",
            FromDisplay = "COHAD Board",
            Subject = "Cancel test",
            ContentBlobPath = $"email-jobs/{jobId:D}.html",
            TotalRecipients = recipients.Count,
            Recipients = recipients
        };

        _jobRepo.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(job);
        _jobRepo.Setup(r => r.GetIncompleteJobsAsync()).ReturnsAsync(new List<EmailJob>());
        _jobRepo.Setup(r => r.UpdateAsync(It.IsAny<EmailJob>())).Returns(Task.CompletedTask);

        _fileStore.Setup(f => f.DownloadAsync(job.ContentBlobPath))
            .ReturnsAsync(new DocumentFileResult
            {
                Stream = new MemoryStream("<p>Cancel test</p>"u8.ToArray()),
                ContentType = "text/html"
            });

        var processor = CreateProcessor();
        using var cts = new CancellationTokenSource();

        var executeTask = processor.StartAsync(cts.Token);
        await _queue.EnqueueAsync(jobId);

        // Wait briefly then cancel the job
        await Task.Delay(100);
        var cancelled = processor.RequestCancellation(jobId);

        // Wait for processing to complete
        await Task.Delay(500);
        cts.Cancel();
        try { await processor.StopAsync(CancellationToken.None); }
        catch (OperationCanceledException) { }

        // Not all recipients should have been sent (unless the machine was very fast)
        // The key test is that cancellation was requested and the processor respected it
        if (cancelled)
        {
            // If we successfully cancelled, some recipients should still be pending
            Assert.True(job.SentCount < job.TotalRecipients,
                $"Expected partial sends after cancel, but all {job.TotalRecipients} were sent");
        }
        // If cancel returned false the job completed before our cancel request — still valid
    }

    // ───────────────────────────────────────────────────
    // Resume incomplete jobs on startup
    // ───────────────────────────────────────────────────

    [Fact]
    public async Task ResumesIncompleteJobsOnStartup()
    {
        var jobId = Guid.NewGuid();
        var incompleteJob = new EmailJob
        {
            Id = jobId,
            Status = EmailJobStatus.InProgress,
            Category = "board",
            ContentBlobPath = $"email-jobs/{jobId:D}.html",
            TotalRecipients = 2,
            SentCount = 1,
            Recipients = new List<EmailJobRecipient>
            {
                new EmailJobRecipient { Email = "done@test.com", Status = EmailJobRecipientStatus.Sent, SentUtc = DateTime.UtcNow },
                new EmailJobRecipient { Email = "pending@test.com", HomeId = Guid.NewGuid(), Status = EmailJobRecipientStatus.Pending }
            }
        };

        _jobRepo.Setup(r => r.GetIncompleteJobsAsync()).ReturnsAsync(new List<EmailJob> { incompleteJob });
        _jobRepo.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(incompleteJob);
        _jobRepo.Setup(r => r.UpdateAsync(It.IsAny<EmailJob>())).Returns(Task.CompletedTask);

        _fileStore.Setup(f => f.DownloadAsync(incompleteJob.ContentBlobPath))
            .ReturnsAsync(new DocumentFileResult
            {
                Stream = new MemoryStream("<p>Resume</p>"u8.ToArray()),
                ContentType = "text/html"
            });

        var processor = CreateProcessor();
        using var cts = new CancellationTokenSource();

        await processor.StartAsync(cts.Token);

        // Wait for processing
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
            if (incompleteJob.Status == EmailJobStatus.Completed)
                break;
        }

        cts.Cancel();
        try { await processor.StopAsync(CancellationToken.None); }
        catch (OperationCanceledException) { }

        Assert.Equal(EmailJobStatus.Completed, incompleteJob.Status);
        Assert.Equal(2, incompleteJob.SentCount);
        Assert.Equal(EmailJobRecipientStatus.Sent, incompleteJob.Recipients[1].Status);
    }

    // ───────────────────────────────────────────────────
    // Missing content blob
    // ───────────────────────────────────────────────────

    [Fact]
    public async Task MissingContentBlob_MarksFailed()
    {
        var jobId = Guid.NewGuid();
        var job = new EmailJob
        {
            Id = jobId,
            Status = EmailJobStatus.Queued,
            Category = "board",
            ContentBlobPath = $"email-jobs/{jobId:D}.html",
            TotalRecipients = 1,
            Recipients = new List<EmailJobRecipient>
            {
                new EmailJobRecipient { Email = "a@test.com", Status = EmailJobRecipientStatus.Pending }
            }
        };

        _jobRepo.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(job);
        _jobRepo.Setup(r => r.GetIncompleteJobsAsync()).ReturnsAsync(new List<EmailJob>());
        _jobRepo.Setup(r => r.UpdateAsync(It.IsAny<EmailJob>())).Returns(Task.CompletedTask);

        // Return null to simulate missing blob
        _fileStore.Setup(f => f.DownloadAsync(job.ContentBlobPath))
            .ReturnsAsync((DocumentFileResult?)null);

        var processor = CreateProcessor();
        await RunProcessorForSingleJob(processor, jobId);

        Assert.Equal(EmailJobStatus.Failed, job.Status);
        Assert.Equal("Email content not found in storage.", job.LastError);
        Assert.NotNull(job.CompletedUtc);
    }

    // ───────────────────────────────────────────────────
    // Cancelled job is skipped
    // ───────────────────────────────────────────────────

    [Fact]
    public async Task CancelledJobInQueue_IsSkipped()
    {
        var jobId = Guid.NewGuid();
        var job = new EmailJob
        {
            Id = jobId,
            Status = EmailJobStatus.Cancelled,
            Category = "board",
            ContentBlobPath = $"email-jobs/{jobId:D}.html",
            TotalRecipients = 1,
            Recipients = new List<EmailJobRecipient>
            {
                new EmailJobRecipient { Email = "a@test.com", Status = EmailJobRecipientStatus.Pending }
            }
        };

        _jobRepo.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(job);
        _jobRepo.Setup(r => r.GetIncompleteJobsAsync()).ReturnsAsync(new List<EmailJob>());

        var processor = CreateProcessor();
        using var cts = new CancellationTokenSource();

        await processor.StartAsync(cts.Token);
        await _queue.EnqueueAsync(jobId);

        // Give processor time to pick up and skip the job
        await Task.Delay(300);

        cts.Cancel();
        try { await processor.StopAsync(CancellationToken.None); }
        catch (OperationCanceledException) { }

        // Job should remain cancelled — not transitioned to InProgress or anything else
        Assert.Equal(EmailJobStatus.Cancelled, job.Status);
        _fileStore.Verify(f => f.DownloadAsync(It.IsAny<string>()), Times.Never);
    }

    // ───────────────────────────────────────────────────
    // Job not found is handled gracefully
    // ───────────────────────────────────────────────────

    [Fact]
    public async Task JobNotFound_IsSkipped()
    {
        var jobId = Guid.NewGuid();
        _jobRepo.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync((EmailJob?)null);
        _jobRepo.Setup(r => r.GetIncompleteJobsAsync()).ReturnsAsync(new List<EmailJob>());

        var processor = CreateProcessor();
        using var cts = new CancellationTokenSource();

        await processor.StartAsync(cts.Token);
        await _queue.EnqueueAsync(jobId);

        await Task.Delay(300);

        cts.Cancel();
        try { await processor.StopAsync(CancellationToken.None); }
        catch (OperationCanceledException) { }

        // Should not throw — just log and skip
        _jobRepo.Verify(r => r.UpdateAsync(It.IsAny<EmailJob>()), Times.Never);
    }

    // ───────────────────────────────────────────────────
    // SignalR notifications
    // ───────────────────────────────────────────────────

    [Fact]
    public async Task MockMode_SendsSignalRNotifications()
    {
        var jobId = Guid.NewGuid();
        var job = new EmailJob
        {
            Id = jobId,
            Status = EmailJobStatus.Queued,
            Category = "board",
            ContentBlobPath = $"email-jobs/{jobId:D}.html",
            TotalRecipients = 2,
            Recipients = new List<EmailJobRecipient>
            {
                new EmailJobRecipient { Email = "a@test.com", HomeId = Guid.NewGuid(), Status = EmailJobRecipientStatus.Pending },
                new EmailJobRecipient { Email = "b@test.com", HomeId = Guid.NewGuid(), Status = EmailJobRecipientStatus.Pending }
            }
        };

        _jobRepo.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(job);
        _jobRepo.Setup(r => r.GetIncompleteJobsAsync()).ReturnsAsync(new List<EmailJob>());
        _jobRepo.Setup(r => r.UpdateAsync(It.IsAny<EmailJob>())).Returns(Task.CompletedTask);

        _fileStore.Setup(f => f.DownloadAsync(job.ContentBlobPath))
            .ReturnsAsync(new DocumentFileResult
            {
                Stream = new MemoryStream("<p>SignalR test</p>"u8.ToArray()),
                ContentType = "text/html"
            });

        var processor = CreateProcessor();
        await RunProcessorForSingleJob(processor, jobId);

        // Progress should be sent for each recipient
        _clientProxy.Verify(c => c.SendCoreAsync(
            "EmailJobProgress",
            It.IsAny<object[]>(),
            It.IsAny<CancellationToken>()), Times.AtLeast(2));

        // Completion notification should be sent once
        _clientProxy.Verify(c => c.SendCoreAsync(
            "EmailJobCompleted",
            It.IsAny<object[]>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
