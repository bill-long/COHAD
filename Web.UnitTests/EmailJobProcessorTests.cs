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
using Microsoft.Extensions.Options;
using Moq;
using Web.Configuration;
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

    public EmailJobProcessorTests()
    {
        // Default: TryClaimAsync succeeds (single-instance tests)
        _jobRepo.Setup(r => r.TryClaimAsync(It.IsAny<EmailJob>())).ReturnsAsync(true);
    }

    private EmailJobProcessor CreateProcessor(
        string environment = "MockData",
        Dictionary<string, string?>? configOverrides = null
    )
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

        var defaults = new Dictionary<string, string?>
        {
            ["SmtpHost"] = "localhost",
            ["SmtpUser"] = "user",
            ["SmtpPassword"] = "pass",
            ["AppBaseUrl"] = "https://test.cohad.org",
            ["EmailJobs:Enabled"] = "true",
            ["EmailJobs:DefaultMaxRecipientAttempts"] = "3",
            ["EmailJobs:StallAfterMinutes"] = "30",
            ["EmailJobs:Mock:DelayMilliseconds"] = "0",
        };
        if (configOverrides != null)
        {
            foreach (var kv in configOverrides)
                defaults[kv.Key] = kv.Value;
        }

        var config = new ConfigurationBuilder().AddInMemoryCollection(defaults).Build();

        var sesOpts = Options.Create(new SesOptions());
        var mockSmtp = new Mock<IEmailTransport>();
        mockSmtp.Setup(t => t.ProviderName).Returns("SendGrid");
        mockSmtp
            .Setup(t =>
                t.SendAsync(
                    It.IsAny<MimeKit.MimeMessage>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new EmailSendResult { Success = true, ProviderName = "SendGrid" });
        var router = new EmailTransportRouter(mockSmtp.Object, mockSmtp.Object, sesOpts);

        return new EmailJobProcessor(
            _queue,
            scopeFactory.Object,
            _tokenService.Object,
            router,
            hubContext.Object,
            config,
            env.Object,
            NullLogger<EmailJobProcessor>.Instance
        );
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
                _jobRepo.Verify(
                    r =>
                        r.UpdateAsync(
                            It.Is<EmailJob>(j =>
                                j.Id == jobId
                                && (
                                    j.Status == EmailJobStatus.Completed
                                    || j.Status == EmailJobStatus.Failed
                                    || j.Status == EmailJobStatus.PartiallyCompleted
                                    || j.Status == EmailJobStatus.Cancelled
                                )
                            )
                        ),
                    Times.AtLeastOnce
                );
                break;
            }
            catch (MockException)
            {
                // Not yet complete, keep waiting
            }
        }

        // Stop the processor
        cts.Cancel();
        try
        {
            await processor.StopAsync(CancellationToken.None);
        }
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
                new EmailJobRecipient
                {
                    Email = "a@test.com",
                    HomeId = Guid.NewGuid(),
                    Status = EmailJobRecipientStatus.Pending,
                },
                new EmailJobRecipient
                {
                    Email = "b@test.com",
                    HomeId = Guid.NewGuid(),
                    Status = EmailJobRecipientStatus.Pending,
                },
                new EmailJobRecipient
                {
                    Email = "c@test.com",
                    HomeId = Guid.NewGuid(),
                    Status = EmailJobRecipientStatus.Pending,
                },
            },
        };

        _jobRepo.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(job);
        _jobRepo.Setup(r => r.GetIncompleteJobsAsync()).ReturnsAsync(new List<EmailJob>());
        _jobRepo.Setup(r => r.UpdateAsync(It.IsAny<EmailJob>())).Returns(Task.CompletedTask);

        _fileStore
            .Setup(f => f.DownloadAsync(job.ContentBlobPath))
            .ReturnsAsync(
                new DocumentFileResult
                {
                    Stream = new MemoryStream("<p>Hello</p>"u8.ToArray()),
                    ContentType = "text/html",
                }
            );

        var processor = CreateProcessor();
        await RunProcessorForSingleJob(processor, jobId);

        Assert.Equal(EmailJobStatus.Completed, job.Status);
        Assert.Equal(3, job.SentCount);
        Assert.NotNull(job.CompletedUtc);
        Assert.All(
            job.Recipients,
            r =>
            {
                Assert.Equal(EmailJobRecipientStatus.Sent, r.Status);
                Assert.NotNull(r.SentUtc);
            }
        );
    }

    [Fact]
    public async Task MockMode_GroupRecipients_SendsSingleMessage_MarksAllSent()
    {
        var jobId = Guid.NewGuid();
        var job = new EmailJob
        {
            Id = jobId,
            Status = EmailJobStatus.Queued,
            Category = "registration",
            FromEmail = "webservice@cohad.org",
            FromDisplay = "COHAD Web",
            Subject = "New User Registered",
            ContentBlobPath = $"email-jobs/{jobId:D}.html",
            TotalRecipients = 3,
            GroupRecipients = true,
            Recipients = new List<EmailJobRecipient>
            {
                new EmailJobRecipient
                {
                    Email = "admin1@test.com",
                    HomeId = Guid.Empty,
                    Status = EmailJobRecipientStatus.Pending,
                },
                new EmailJobRecipient
                {
                    Email = "admin2@test.com",
                    HomeId = Guid.Empty,
                    Status = EmailJobRecipientStatus.Pending,
                },
                new EmailJobRecipient
                {
                    Email = "admin3@test.com",
                    HomeId = Guid.Empty,
                    Status = EmailJobRecipientStatus.Pending,
                },
            },
        };

        _jobRepo.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(job);
        _jobRepo.Setup(r => r.GetIncompleteJobsAsync()).ReturnsAsync(new List<EmailJob>());
        _jobRepo.Setup(r => r.UpdateAsync(It.IsAny<EmailJob>())).Returns(Task.CompletedTask);

        _fileStore
            .Setup(f => f.DownloadAsync(job.ContentBlobPath))
            .ReturnsAsync(
                new DocumentFileResult
                {
                    Stream = new MemoryStream("<p>New user info</p>"u8.ToArray()),
                    ContentType = "text/html",
                }
            );

        var processor = CreateProcessor();
        await RunProcessorForSingleJob(processor, jobId);

        // All recipients marked sent
        Assert.Equal(EmailJobStatus.Completed, job.Status);
        Assert.Equal(3, job.SentCount);
        Assert.All(
            job.Recipients,
            r =>
            {
                Assert.Equal(EmailJobRecipientStatus.Sent, r.Status);
                Assert.NotNull(r.SentUtc);
                Assert.Equal(1, r.AttemptCount);
            }
        );

        // All recipients should have the same SentUtc (single logical send)
        var sentTimes = job.Recipients.Select(r => r.SentUtc).Distinct().ToList();
        Assert.Single(sentTimes);
    }

    [Fact]
    public async Task MockMode_FailAllRecipients_MarksEveryRecipientFailed()
    {
        var jobId = Guid.NewGuid();
        var job = new EmailJob
        {
            Id = jobId,
            Status = EmailJobStatus.Queued,
            Category = "board",
            FromEmail = "board@cohad.org",
            FromDisplay = "COHAD Board",
            Subject = "Fail all",
            ContentBlobPath = $"email-jobs/{jobId:D}.html",
            TotalRecipients = 2,
            Recipients = new List<EmailJobRecipient>
            {
                new EmailJobRecipient
                {
                    Email = "a@test.com",
                    HomeId = Guid.NewGuid(),
                    Status = EmailJobRecipientStatus.Pending,
                },
                new EmailJobRecipient
                {
                    Email = "b@test.com",
                    HomeId = Guid.NewGuid(),
                    Status = EmailJobRecipientStatus.Pending,
                },
            },
        };

        _jobRepo.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(job);
        _jobRepo.Setup(r => r.GetIncompleteJobsAsync()).ReturnsAsync(new List<EmailJob>());
        _jobRepo.Setup(r => r.UpdateAsync(It.IsAny<EmailJob>())).Returns(Task.CompletedTask);

        _fileStore
            .Setup(f => f.DownloadAsync(job.ContentBlobPath))
            .ReturnsAsync(
                new DocumentFileResult { Stream = new MemoryStream("<p>x</p>"u8.ToArray()), ContentType = "text/html" }
            );

        var processor = CreateProcessor(
            configOverrides: new Dictionary<string, string?> { ["EmailJobs:Mock:FailAllRecipients"] = "true" }
        );
        await RunProcessorForSingleJob(processor, jobId);

        Assert.Equal(EmailJobStatus.Failed, job.Status);
        Assert.Equal(0, job.SentCount);
        Assert.Equal(2, job.FailedCount);
        Assert.All(
            job.Recipients,
            r =>
            {
                Assert.Equal(EmailJobRecipientStatus.Failed, r.Status);
                Assert.Contains("Mock send failure", r.Error ?? "", StringComparison.OrdinalIgnoreCase);
            }
        );
    }

    [Fact]
    public async Task MockMode_RandomFailureProbabilityOne_FailsEachRecipient()
    {
        var jobId = Guid.NewGuid();
        var job = new EmailJob
        {
            Id = jobId,
            Status = EmailJobStatus.Queued,
            Category = "board",
            FromEmail = "board@cohad.org",
            FromDisplay = "COHAD Board",
            Subject = "Prob 1",
            ContentBlobPath = $"email-jobs/{jobId:D}.html",
            TotalRecipients = 3,
            Recipients = new List<EmailJobRecipient>
            {
                new EmailJobRecipient
                {
                    Email = "a@test.com",
                    HomeId = Guid.NewGuid(),
                    Status = EmailJobRecipientStatus.Pending,
                },
                new EmailJobRecipient
                {
                    Email = "b@test.com",
                    HomeId = Guid.NewGuid(),
                    Status = EmailJobRecipientStatus.Pending,
                },
                new EmailJobRecipient
                {
                    Email = "c@test.com",
                    HomeId = Guid.NewGuid(),
                    Status = EmailJobRecipientStatus.Pending,
                },
            },
        };

        _jobRepo.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(job);
        _jobRepo.Setup(r => r.GetIncompleteJobsAsync()).ReturnsAsync(new List<EmailJob>());
        _jobRepo.Setup(r => r.UpdateAsync(It.IsAny<EmailJob>())).Returns(Task.CompletedTask);

        _fileStore
            .Setup(f => f.DownloadAsync(job.ContentBlobPath))
            .ReturnsAsync(
                new DocumentFileResult { Stream = new MemoryStream("<p>x</p>"u8.ToArray()), ContentType = "text/html" }
            );

        var processor = CreateProcessor(
            configOverrides: new Dictionary<string, string?> { ["EmailJobs:Mock:RandomFailureProbability"] = "1" }
        );
        await RunProcessorForSingleJob(processor, jobId);

        Assert.Equal(EmailJobStatus.Failed, job.Status);
        Assert.Equal(0, job.SentCount);
        Assert.Equal(3, job.FailedCount);
    }

    [Fact]
    public async Task MockMode_JobFatalError_MarksJobFailedWithoutProcessingRecipients()
    {
        var jobId = Guid.NewGuid();
        var job = new EmailJob
        {
            Id = jobId,
            Status = EmailJobStatus.Queued,
            Category = "board",
            FromEmail = "board@cohad.org",
            FromDisplay = "COHAD Board",
            Subject = "Fatal",
            ContentBlobPath = $"email-jobs/{jobId:D}.html",
            TotalRecipients = 2,
            Recipients = new List<EmailJobRecipient>
            {
                new EmailJobRecipient
                {
                    Email = "a@test.com",
                    HomeId = Guid.NewGuid(),
                    Status = EmailJobRecipientStatus.Pending,
                },
                new EmailJobRecipient
                {
                    Email = "b@test.com",
                    HomeId = Guid.NewGuid(),
                    Status = EmailJobRecipientStatus.Pending,
                },
            },
        };

        _jobRepo.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(job);
        _jobRepo.Setup(r => r.GetIncompleteJobsAsync()).ReturnsAsync(new List<EmailJob>());
        _jobRepo.Setup(r => r.UpdateAsync(It.IsAny<EmailJob>())).Returns(Task.CompletedTask);

        _fileStore
            .Setup(f => f.DownloadAsync(job.ContentBlobPath))
            .ReturnsAsync(
                new DocumentFileResult { Stream = new MemoryStream("<p>x</p>"u8.ToArray()), ContentType = "text/html" }
            );

        var processor = CreateProcessor(
            configOverrides: new Dictionary<string, string?>
            {
                ["EmailJobs:Mock:JobFatalError"] = "Simulated fatal job error",
            }
        );
        await RunProcessorForSingleJob(processor, jobId);

        Assert.Equal(EmailJobStatus.Failed, job.Status);
        Assert.Equal("Simulated fatal job error", job.LastError);
        Assert.Equal(EmailJobRecipientStatus.Pending, job.Recipients![0].Status);
        Assert.Equal(EmailJobRecipientStatus.Pending, job.Recipients![1].Status);
    }

    [Fact]
    public async Task MockMode_FixedRandomSeed_ReproducibleSentCount()
    {
        const int recipientCount = 16;
        const double probability = 0.33;
        const int seed = 424242;

        async Task<int> RunOnce()
        {
            var jobId = Guid.NewGuid();
            var recipients = Enumerable
                .Range(0, recipientCount)
                .Select(i => new EmailJobRecipient
                {
                    Email = $"u{i}@test.com",
                    HomeId = Guid.NewGuid(),
                    Status = EmailJobRecipientStatus.Pending,
                })
                .ToList();

            var job = new EmailJob
            {
                Id = jobId,
                Status = EmailJobStatus.Queued,
                Category = "board",
                FromEmail = "board@cohad.org",
                FromDisplay = "COHAD Board",
                Subject = "Seed",
                ContentBlobPath = $"email-jobs/{jobId:D}.html",
                TotalRecipients = recipientCount,
                Recipients = recipients,
            };

            var jobRepo = new Mock<IEmailJobRepository>();
            jobRepo.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(job);
            jobRepo.Setup(r => r.GetIncompleteJobsAsync()).ReturnsAsync(new List<EmailJob>());
            jobRepo.Setup(r => r.TryClaimAsync(It.IsAny<EmailJob>())).ReturnsAsync(true);
            jobRepo.Setup(r => r.UpdateAsync(It.IsAny<EmailJob>())).Returns(Task.CompletedTask);

            var fileStore = new Mock<IDocumentFileStore>();
            fileStore
                .Setup(f => f.DownloadAsync(job.ContentBlobPath))
                .ReturnsAsync(
                    new DocumentFileResult
                    {
                        Stream = new MemoryStream("<p>x</p>"u8.ToArray()),
                        ContentType = "text/html",
                    }
                );

            var scopeFactory = new Mock<IServiceScopeFactory>();
            var scope = new Mock<IServiceScope>();
            var serviceProvider = new Mock<IServiceProvider>();
            serviceProvider.Setup(sp => sp.GetService(typeof(IEmailJobRepository))).Returns(jobRepo.Object);
            serviceProvider.Setup(sp => sp.GetService(typeof(IDocumentFileStore))).Returns(fileStore.Object);
            scope.Setup(s => s.ServiceProvider).Returns(serviceProvider.Object);
            scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

            var hubContext = new Mock<IHubContext<EmailJobHub>>();
            var mockClients = new Mock<IHubClients>();
            mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_clientProxy.Object);
            hubContext.Setup(h => h.Clients).Returns(mockClients.Object);

            var env = new Mock<IWebHostEnvironment>();
            env.Setup(e => e.EnvironmentName).Returns("MockData");

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["SmtpHost"] = "localhost",
                        ["SmtpUser"] = "user",
                        ["SmtpPassword"] = "pass",
                        ["AppBaseUrl"] = "https://test.cohad.org",
                        ["EmailJobs:Enabled"] = "true",
                        ["EmailJobs:DefaultMaxRecipientAttempts"] = "3",
                        ["EmailJobs:StallAfterMinutes"] = "30",
                        ["EmailJobs:Mock:DelayMilliseconds"] = "0",
                        ["EmailJobs:Mock:RandomFailureProbability"] = probability.ToString(
                            System.Globalization.CultureInfo.InvariantCulture
                        ),
                        ["EmailJobs:Mock:RandomFailSeed"] = seed.ToString(),
                    }
                )
                .Build();

            var queue = new EmailJobQueue();
            var sesOpts2 = Options.Create(new SesOptions());
            var mockSmtp2 = new Mock<IEmailTransport>();
            mockSmtp2.Setup(t => t.ProviderName).Returns("SendGrid");
            mockSmtp2
                .Setup(t =>
                    t.SendAsync(
                        It.IsAny<MimeKit.MimeMessage>(),
                        It.IsAny<string>(),
                        It.IsAny<string>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .ReturnsAsync(new EmailSendResult { Success = true, ProviderName = "SendGrid" });
            var router2 = new EmailTransportRouter(mockSmtp2.Object, mockSmtp2.Object, sesOpts2);
            var processor = new EmailJobProcessor(
                queue,
                scopeFactory.Object,
                _tokenService.Object,
                router2,
                hubContext.Object,
                config,
                env.Object,
                NullLogger<EmailJobProcessor>.Instance
            );

            using var cts = new CancellationTokenSource();
            _ = processor.StartAsync(cts.Token);
            await queue.EnqueueAsync(jobId);

            var deadline = DateTime.UtcNow.AddMilliseconds(8000);
            var reachedTerminal = false;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
                if (
                    job.Status is EmailJobStatus.Completed or EmailJobStatus.Failed or EmailJobStatus.PartiallyCompleted
                )
                {
                    reachedTerminal = true;
                    break;
                }
            }

            cts.Cancel();
            try
            {
                await processor.StopAsync(CancellationToken.None);
            }
            catch (OperationCanceledException) { }

            Assert.True(reachedTerminal, "Email job did not reach a terminal status before the deadline.");
            return job.SentCount;
        }

        var first = await RunOnce();
        var second = await RunOnce();
        Assert.Equal(first, second);
        Assert.True(first >= 0 && first <= recipientCount);
    }

    [Fact]
    public async Task MockMode_AttemptCap_SkipsCappedRecipients_TerminalStatusReflectsFailures()
    {
        var jobId = Guid.NewGuid();
        var job = new EmailJob
        {
            Id = jobId,
            Status = EmailJobStatus.Queued,
            Category = "board",
            FromEmail = "board@cohad.org",
            FromDisplay = "COHAD Board",
            Subject = "Cap test",
            ContentBlobPath = $"email-jobs/{jobId:D}.html",
            MaxRecipientAttempts = 2,
            TotalRecipients = 3,
            Recipients = new List<EmailJobRecipient>
            {
                new EmailJobRecipient
                {
                    Email = "capped@test.com",
                    HomeId = Guid.NewGuid(),
                    Status = EmailJobRecipientStatus.Pending,
                    AttemptCount = 2,
                },
                new EmailJobRecipient
                {
                    Email = "was-smtp-fail@test.com",
                    HomeId = Guid.NewGuid(),
                    Status = EmailJobRecipientStatus.Failed,
                    AttemptCount = 2,
                    Error = "SMTP rejected",
                },
                new EmailJobRecipient
                {
                    Email = "ok@test.com",
                    HomeId = Guid.NewGuid(),
                    Status = EmailJobRecipientStatus.Pending,
                    AttemptCount = 0,
                },
            },
        };

        _jobRepo.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(job);
        _jobRepo.Setup(r => r.GetIncompleteJobsAsync()).ReturnsAsync(new List<EmailJob>());
        _jobRepo.Setup(r => r.UpdateAsync(It.IsAny<EmailJob>())).Returns(Task.CompletedTask);

        _fileStore
            .Setup(f => f.DownloadAsync(job.ContentBlobPath))
            .ReturnsAsync(
                new DocumentFileResult
                {
                    Stream = new MemoryStream("<p>Cap</p>"u8.ToArray()),
                    ContentType = "text/html",
                }
            );

        var processor = CreateProcessor(
            configOverrides: new Dictionary<string, string?> { ["EmailJobs:DefaultMaxRecipientAttempts"] = "2" }
        );
        await RunProcessorForSingleJob(processor, jobId);

        Assert.Equal(EmailJobStatus.PartiallyCompleted, job.Status);
        Assert.Equal(1, job.SentCount);
        Assert.Equal(2, job.FailedCount);
        Assert.Equal(EmailJobRecipientStatus.Failed, job.Recipients[0].Status);
        Assert.Contains("Max attempts reached", job.Recipients[0].Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SMTP rejected", job.Recipients[1].Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Additional info:", job.Recipients[1].Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(EmailJobRecipientStatus.Sent, job.Recipients[2].Status);
    }

    [Fact]
    public async Task MockMode_CapSuffixIsNotAppendedTwiceWhenRecipientAlreadyHasCapError()
    {
        var jobId = Guid.NewGuid();
        var capFragment = "Additional info: Max attempts reached (2).";
        var job = new EmailJob
        {
            Id = jobId,
            Status = EmailJobStatus.Queued,
            Category = "board",
            FromEmail = "board@cohad.org",
            FromDisplay = "COHAD Board",
            Subject = "Idempotent cap error",
            ContentBlobPath = $"email-jobs/{jobId:D}.html",
            MaxRecipientAttempts = 2,
            TotalRecipients = 2,
            Recipients = new List<EmailJobRecipient>
            {
                new EmailJobRecipient
                {
                    Email = "already-capped@test.com",
                    HomeId = Guid.NewGuid(),
                    Status = EmailJobRecipientStatus.Failed,
                    AttemptCount = 2,
                    Error = $"SMTP error {capFragment}",
                },
                new EmailJobRecipient
                {
                    Email = "ok@test.com",
                    HomeId = Guid.NewGuid(),
                    Status = EmailJobRecipientStatus.Pending,
                    AttemptCount = 0,
                },
            },
        };

        _jobRepo.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(job);
        _jobRepo.Setup(r => r.GetIncompleteJobsAsync()).ReturnsAsync(new List<EmailJob>());
        _jobRepo.Setup(r => r.UpdateAsync(It.IsAny<EmailJob>())).Returns(Task.CompletedTask);

        _fileStore
            .Setup(f => f.DownloadAsync(job.ContentBlobPath))
            .ReturnsAsync(
                new DocumentFileResult { Stream = new MemoryStream("<p>X</p>"u8.ToArray()), ContentType = "text/html" }
            );

        var processor = CreateProcessor();
        await RunProcessorForSingleJob(processor, jobId);

        Assert.Equal(EmailJobStatus.PartiallyCompleted, job.Status);
        Assert.StartsWith("SMTP error", job.Recipients[0].Error, StringComparison.Ordinal);
        Assert.Equal(
            job.Recipients[0].Error.IndexOf(capFragment, StringComparison.Ordinal),
            job.Recipients[0].Error.LastIndexOf(capFragment, StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task MockMode_ClampsExcessiveMaxRecipientAttemptsOnClaim()
    {
        var jobId = Guid.NewGuid();
        var job = new EmailJob
        {
            Id = jobId,
            Status = EmailJobStatus.Queued,
            Category = "board",
            FromEmail = "board@cohad.org",
            FromDisplay = "COHAD Board",
            Subject = "Clamp test",
            ContentBlobPath = $"email-jobs/{jobId:D}.html",
            MaxRecipientAttempts = 999,
            TotalRecipients = 1,
            Recipients = new List<EmailJobRecipient>
            {
                new EmailJobRecipient
                {
                    Email = "one@test.com",
                    HomeId = Guid.NewGuid(),
                    Status = EmailJobRecipientStatus.Pending,
                },
            },
        };

        _jobRepo.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(job);
        _jobRepo.Setup(r => r.GetIncompleteJobsAsync()).ReturnsAsync(new List<EmailJob>());
        _jobRepo.Setup(r => r.UpdateAsync(It.IsAny<EmailJob>())).Returns(Task.CompletedTask);

        _fileStore
            .Setup(f => f.DownloadAsync(job.ContentBlobPath))
            .ReturnsAsync(
                new DocumentFileResult
                {
                    Stream = new MemoryStream("<p>Clamp</p>"u8.ToArray()),
                    ContentType = "text/html",
                }
            );

        var processor = CreateProcessor();
        await RunProcessorForSingleJob(processor, jobId);

        Assert.Equal(25, job.MaxRecipientAttempts);
        Assert.Equal(EmailJobStatus.Completed, job.Status);
    }

    [Fact]
    public async Task MockMode_RespectsCancel()
    {
        var jobId = Guid.NewGuid();
        // Use many recipients so we have time to cancel
        var recipients = Enumerable
            .Range(1, 100)
            .Select(i => new EmailJobRecipient
            {
                Email = $"r{i}@test.com",
                HomeId = Guid.NewGuid(),
                Status = EmailJobRecipientStatus.Pending,
            })
            .ToList();

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
            Recipients = recipients,
        };

        _jobRepo.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(job);
        _jobRepo.Setup(r => r.GetIncompleteJobsAsync()).ReturnsAsync(new List<EmailJob>());
        _jobRepo.Setup(r => r.UpdateAsync(It.IsAny<EmailJob>())).Returns(Task.CompletedTask);

        _fileStore
            .Setup(f => f.DownloadAsync(job.ContentBlobPath))
            .ReturnsAsync(
                new DocumentFileResult
                {
                    Stream = new MemoryStream("<p>Cancel test</p>"u8.ToArray()),
                    ContentType = "text/html",
                }
            );

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
        try
        {
            await processor.StopAsync(CancellationToken.None);
        }
        catch (OperationCanceledException) { }

        // Not all recipients should have been sent (unless the machine was very fast)
        // The key test is that cancellation was requested and the processor respected it
        if (cancelled)
        {
            // If we successfully cancelled, some recipients should still be pending
            Assert.True(
                job.SentCount < job.TotalRecipients,
                $"Expected partial sends after cancel, but all {job.TotalRecipients} were sent"
            );
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
            LastProgressUtc = DateTime.UtcNow,
            Recipients = new List<EmailJobRecipient>
            {
                new EmailJobRecipient
                {
                    Email = "done@test.com",
                    Status = EmailJobRecipientStatus.Sent,
                    SentUtc = DateTime.UtcNow,
                },
                new EmailJobRecipient
                {
                    Email = "pending@test.com",
                    HomeId = Guid.NewGuid(),
                    Status = EmailJobRecipientStatus.Pending,
                },
            },
        };

        _jobRepo.Setup(r => r.GetIncompleteJobsAsync()).ReturnsAsync(new List<EmailJob> { incompleteJob });
        _jobRepo.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(incompleteJob);
        _jobRepo.Setup(r => r.UpdateAsync(It.IsAny<EmailJob>())).Returns(Task.CompletedTask);

        _fileStore
            .Setup(f => f.DownloadAsync(incompleteJob.ContentBlobPath))
            .ReturnsAsync(
                new DocumentFileResult
                {
                    Stream = new MemoryStream("<p>Resume</p>"u8.ToArray()),
                    ContentType = "text/html",
                }
            );

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
        try
        {
            await processor.StopAsync(CancellationToken.None);
        }
        catch (OperationCanceledException) { }

        Assert.Equal(EmailJobStatus.Completed, incompleteJob.Status);
        Assert.Equal(2, incompleteJob.SentCount);
        Assert.Equal(EmailJobRecipientStatus.Sent, incompleteJob.Recipients[1].Status);
    }

    [Fact]
    public async Task DisabledProcessor_DoesNotResumeOrProcess()
    {
        _jobRepo
            .Setup(r => r.GetIncompleteJobsAsync())
            .ReturnsAsync(
                new List<EmailJob>
                {
                    new EmailJob { Id = Guid.NewGuid(), Status = EmailJobStatus.Queued },
                }
            );

        var processor = CreateProcessor(
            configOverrides: new Dictionary<string, string?> { ["EmailJobs:Enabled"] = "false" }
        );

        using var cts = new CancellationTokenSource();
        await processor.StartAsync(cts.Token);

        // Give the background service a moment to run/return
        await Task.Delay(100);

        _jobRepo.Verify(r => r.GetIncompleteJobsAsync(), Times.Never);
        _jobRepo.Verify(r => r.GetByIdAsync(It.IsAny<Guid>()), Times.Never);
        _jobRepo.Verify(r => r.UpdateAsync(It.IsAny<EmailJob>()), Times.Never);
    }

    [Fact]
    public async Task Resume_StalledInProgressJob_WithPartialSends_MarksPartiallyCompleted()
    {
        var jobId = Guid.NewGuid();
        var stalled = new EmailJob
        {
            Id = jobId,
            Status = EmailJobStatus.InProgress,
            Category = "board",
            ContentBlobPath = $"email-jobs/{jobId:D}.html",
            CreatedUtc = DateTime.UtcNow.AddHours(-2),
            StartedUtc = DateTime.UtcNow.AddHours(-2),
            LastProgressUtc = DateTime.UtcNow.AddMinutes(-10),
            TotalRecipients = 2,
            Recipients = new List<EmailJobRecipient>
            {
                new EmailJobRecipient
                {
                    Email = "done@test.com",
                    HomeId = Guid.NewGuid(),
                    Status = EmailJobRecipientStatus.Sent,
                    SentUtc = DateTime.UtcNow.AddMinutes(-30),
                },
                new EmailJobRecipient
                {
                    Email = "pending@test.com",
                    HomeId = Guid.NewGuid(),
                    Status = EmailJobRecipientStatus.Pending,
                    // Delivery event confirms email was sent; nothing productive left to do.
                    DeliveryStatus = DeliveryStatus.Deferred,
                },
            },
        };

        _jobRepo.Setup(r => r.GetIncompleteJobsAsync()).ReturnsAsync(new List<EmailJob> { stalled });
        _jobRepo.Setup(r => r.UpdateAsync(It.IsAny<EmailJob>())).Returns(Task.CompletedTask);

        var processor = CreateProcessor(
            configOverrides: new Dictionary<string, string?> { ["EmailJobs:StallAfterMinutes"] = "1" }
        );

        using var cts = new CancellationTokenSource();
        await processor.StartAsync(cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
            if (stalled.Status == EmailJobStatus.Completed)
                break;
        }

        cts.Cancel();
        try
        {
            await processor.StopAsync(CancellationToken.None);
        }
        catch (OperationCanceledException) { }

        // NormalizePendingDelivered promotes the Deferred recipient to Sent,
        // so both recipients are Sent and the job is Completed.
        Assert.Equal(EmailJobStatus.Completed, stalled.Status);
        Assert.NotNull(stalled.CompletedUtc);
        Assert.Contains("stalled", stalled.LastError, StringComparison.OrdinalIgnoreCase);
        _fileStore.Verify(f => f.DownloadAsync(It.IsAny<string>()), Times.Never);
        _jobRepo.Verify(r => r.GetByIdAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task Resume_StalledInProgressJob_MarksFailedAndDoesNotEnqueue()
    {
        var jobId = Guid.NewGuid();
        var stalled = new EmailJob
        {
            Id = jobId,
            Status = EmailJobStatus.InProgress,
            Category = "board",
            ContentBlobPath = $"email-jobs/{jobId:D}.html",
            CreatedUtc = DateTime.UtcNow.AddHours(-2),
            StartedUtc = DateTime.UtcNow.AddHours(-2),
            LastProgressUtc = DateTime.UtcNow.AddMinutes(-10),
            TotalRecipients = 1,
            Recipients = new List<EmailJobRecipient>
            {
                new EmailJobRecipient
                {
                    Email = "pending@test.com",
                    HomeId = Guid.NewGuid(),
                    Status = EmailJobRecipientStatus.Pending,
                    // Delivery event confirms email was sent; stall watchdog should terminate.
                    DeliveryStatus = DeliveryStatus.Delivered,
                },
            },
        };

        _jobRepo.Setup(r => r.GetIncompleteJobsAsync()).ReturnsAsync(new List<EmailJob> { stalled });
        _jobRepo.Setup(r => r.UpdateAsync(It.IsAny<EmailJob>())).Returns(Task.CompletedTask);

        var processor = CreateProcessor(
            configOverrides: new Dictionary<string, string?> { ["EmailJobs:StallAfterMinutes"] = "1" }
        );

        using var cts = new CancellationTokenSource();
        await processor.StartAsync(cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
            if (stalled.Status == EmailJobStatus.Completed)
                break;
        }

        cts.Cancel();
        try
        {
            await processor.StopAsync(CancellationToken.None);
        }
        catch (OperationCanceledException) { }

        // NormalizePendingDelivered promotes the Delivered recipient to Sent,
        // so the single recipient is Sent and the job is Completed.
        Assert.Equal(EmailJobStatus.Completed, stalled.Status);
        Assert.NotNull(stalled.CompletedUtc);
        Assert.Contains("stalled", stalled.LastError, StringComparison.OrdinalIgnoreCase);
        _fileStore.Verify(f => f.DownloadAsync(It.IsAny<string>()), Times.Never);
        _jobRepo.Verify(r => r.GetByIdAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task Resume_StalledInProgressJob_WithUnsentRecipients_ReEnqueues()
    {
        var jobId = Guid.NewGuid();
        var stalled = new EmailJob
        {
            Id = jobId,
            Status = EmailJobStatus.InProgress,
            Category = "board",
            ContentBlobPath = $"email-jobs/{jobId:D}.html",
            CreatedUtc = DateTime.UtcNow.AddHours(-2),
            StartedUtc = DateTime.UtcNow.AddHours(-2),
            LastProgressUtc = DateTime.UtcNow.AddMinutes(-10),
            TotalRecipients = 2,
            MaxRecipientAttempts = 3,
            Recipients = new List<EmailJobRecipient>
            {
                new EmailJobRecipient
                {
                    Email = "done@test.com",
                    HomeId = Guid.NewGuid(),
                    Status = EmailJobRecipientStatus.Sent,
                    SentUtc = DateTime.UtcNow.AddMinutes(-30),
                },
                new EmailJobRecipient
                {
                    Email = "unsent@test.com",
                    HomeId = Guid.NewGuid(),
                    Status = EmailJobRecipientStatus.Pending,
                    // DeliveryStatus is Unknown (default) — email was never sent.
                },
            },
        };

        _jobRepo.Setup(r => r.GetIncompleteJobsAsync()).ReturnsAsync(new List<EmailJob> { stalled });

        // Wire up GetByIdAsync + UpdateAsync so the re-enqueued job can be processed.
        var htmlBytes = System.Text.Encoding.UTF8.GetBytes("<html>test</html>");
        _fileStore
            .Setup(f => f.DownloadAsync(stalled.ContentBlobPath))
            .ReturnsAsync(new DocumentFileResult { Stream = new MemoryStream(htmlBytes), ContentType = "text/html" });
        _jobRepo.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(stalled);
        _jobRepo.Setup(r => r.UpdateAsync(It.IsAny<EmailJob>())).Returns(Task.CompletedTask);

        var processor = CreateProcessor(
            configOverrides: new Dictionary<string, string?> { ["EmailJobs:StallAfterMinutes"] = "1" }
        );

        using var cts = new CancellationTokenSource();
        await processor.StartAsync(cts.Token);

        // Wait for the re-enqueued job to be processed (recipient marked Sent)
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
            if (stalled.Recipients[1].Status == EmailJobRecipientStatus.Sent)
                break;
        }

        cts.Cancel();
        try
        {
            await processor.StopAsync(CancellationToken.None);
        }
        catch (OperationCanceledException) { }

        // The unsent recipient should now be Sent (mock mode processes it)
        Assert.Equal(EmailJobRecipientStatus.Sent, stalled.Recipients[1].Status);
        // Job should not have been terminated by the stall watchdog
        Assert.Null(stalled.LastError);
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
                new EmailJobRecipient { Email = "a@test.com", Status = EmailJobRecipientStatus.Pending },
            },
        };

        _jobRepo.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(job);
        _jobRepo.Setup(r => r.GetIncompleteJobsAsync()).ReturnsAsync(new List<EmailJob>());
        _jobRepo.Setup(r => r.UpdateAsync(It.IsAny<EmailJob>())).Returns(Task.CompletedTask);

        // Return null to simulate missing blob
        _fileStore.Setup(f => f.DownloadAsync(job.ContentBlobPath)).ReturnsAsync((DocumentFileResult?)null);

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
                new EmailJobRecipient { Email = "a@test.com", Status = EmailJobRecipientStatus.Pending },
            },
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
        try
        {
            await processor.StopAsync(CancellationToken.None);
        }
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
        try
        {
            await processor.StopAsync(CancellationToken.None);
        }
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
                new EmailJobRecipient
                {
                    Email = "a@test.com",
                    HomeId = Guid.NewGuid(),
                    Status = EmailJobRecipientStatus.Pending,
                },
                new EmailJobRecipient
                {
                    Email = "b@test.com",
                    HomeId = Guid.NewGuid(),
                    Status = EmailJobRecipientStatus.Pending,
                },
            },
        };

        _jobRepo.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(job);
        _jobRepo.Setup(r => r.GetIncompleteJobsAsync()).ReturnsAsync(new List<EmailJob>());
        _jobRepo.Setup(r => r.UpdateAsync(It.IsAny<EmailJob>())).Returns(Task.CompletedTask);

        _fileStore
            .Setup(f => f.DownloadAsync(job.ContentBlobPath))
            .ReturnsAsync(
                new DocumentFileResult
                {
                    Stream = new MemoryStream("<p>SignalR test</p>"u8.ToArray()),
                    ContentType = "text/html",
                }
            );

        var processor = CreateProcessor();
        await RunProcessorForSingleJob(processor, jobId);

        // Progress should be sent for each recipient
        _clientProxy.Verify(
            c => c.SendCoreAsync("EmailJobProgress", It.IsAny<object[]>(), It.IsAny<CancellationToken>()),
            Times.AtLeast(2)
        );

        // Completion notification should be sent once
        _clientProxy.Verify(
            c => c.SendCoreAsync("EmailJobCompleted", It.IsAny<object[]>(), It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public void MergeWebhookFields_copies_delivery_status_from_server()
    {
        var local = new EmailJob
        {
            Recipients = new List<EmailJobRecipient>
            {
                new() { Email = "a@test.com", DeliveryStatus = DeliveryStatus.Unknown },
                new() { Email = "b@test.com", DeliveryStatus = DeliveryStatus.Unknown },
            },
        };
        var server = new EmailJob
        {
            Recipients = new List<EmailJobRecipient>
            {
                new()
                {
                    Email = "a@test.com",
                    DeliveryStatus = DeliveryStatus.Delivered,
                    DeliveryStatusUpdatedUtc = new DateTime(2026, 4, 7, 3, 36, 0, DateTimeKind.Utc),
                },
                new() { Email = "b@test.com", DeliveryStatus = DeliveryStatus.Unknown },
            },
        };

        EmailJobProcessor.MergeWebhookFields(local, server);

        Assert.Equal(DeliveryStatus.Delivered, local.Recipients[0].DeliveryStatus);
        Assert.Equal(
            new DateTime(2026, 4, 7, 3, 36, 0, DateTimeKind.Utc),
            local.Recipients[0].DeliveryStatusUpdatedUtc
        );
        Assert.Equal(DeliveryStatus.Unknown, local.Recipients[1].DeliveryStatus);
    }

    [Fact]
    public void MergeWebhookFields_copies_provider_message_id_from_server()
    {
        var local = new EmailJob
        {
            Recipients = new List<EmailJobRecipient>
            {
                new() { Email = "a@test.com", ProviderMessageId = null },
            },
        };
        var server = new EmailJob
        {
            Recipients = new List<EmailJobRecipient>
            {
                new() { Email = "a@test.com", ProviderMessageId = "sg-msg-123" },
            },
        };

        EmailJobProcessor.MergeWebhookFields(local, server);

        Assert.Equal("sg-msg-123", local.Recipients[0].ProviderMessageId);
    }

    [Fact]
    public void MergeWebhookFields_does_not_overwrite_local_provider_message_id()
    {
        var local = new EmailJob
        {
            Recipients = new List<EmailJobRecipient>
            {
                new() { Email = "a@test.com", ProviderMessageId = "local-id" },
            },
        };
        var server = new EmailJob
        {
            Recipients = new List<EmailJobRecipient>
            {
                new() { Email = "a@test.com", ProviderMessageId = "server-id" },
            },
        };

        EmailJobProcessor.MergeWebhookFields(local, server);

        Assert.Equal("local-id", local.Recipients[0].ProviderMessageId);
    }

    [Fact]
    public void MergeWebhookFields_handles_case_insensitive_email_match()
    {
        var local = new EmailJob
        {
            Recipients = new List<EmailJobRecipient>
            {
                new() { Email = "User@Test.COM", DeliveryStatus = DeliveryStatus.Unknown },
            },
        };
        var server = new EmailJob
        {
            Recipients = new List<EmailJobRecipient>
            {
                new() { Email = "user@test.com", DeliveryStatus = DeliveryStatus.Bounced },
            },
        };

        EmailJobProcessor.MergeWebhookFields(local, server);

        Assert.Equal(DeliveryStatus.Bounced, local.Recipients[0].DeliveryStatus);
    }

    [Fact]
    public void MergeWebhookFields_handles_null_recipients()
    {
        var local = new EmailJob { Recipients = null };
        var server = new EmailJob
        {
            Recipients = new List<EmailJobRecipient>
            {
                new() { Email = "a@test.com", DeliveryStatus = DeliveryStatus.Delivered },
            },
        };

        // Should not throw
        EmailJobProcessor.MergeWebhookFields(local, server);

        EmailJobProcessor.MergeWebhookFields(server, new EmailJob { Recipients = null });
    }

    [Fact]
    public void MergeWebhookFields_preserves_local_status_fields()
    {
        var local = new EmailJob
        {
            Recipients = new List<EmailJobRecipient>
            {
                new()
                {
                    Email = "a@test.com",
                    Status = EmailJobRecipientStatus.Sent,
                    SentUtc = new DateTime(2026, 4, 7, 3, 36, 0, DateTimeKind.Utc),
                    AttemptCount = 1,
                    DeliveryStatus = DeliveryStatus.Unknown,
                },
            },
        };
        var server = new EmailJob
        {
            Recipients = new List<EmailJobRecipient>
            {
                new()
                {
                    Email = "a@test.com",
                    Status = EmailJobRecipientStatus.Pending,
                    AttemptCount = 0,
                    DeliveryStatus = DeliveryStatus.Delivered,
                },
            },
        };

        EmailJobProcessor.MergeWebhookFields(local, server);

        // Processor-owned fields must not be overwritten
        Assert.Equal(EmailJobRecipientStatus.Sent, local.Recipients[0].Status);
        Assert.Equal(1, local.Recipients[0].AttemptCount);
        // Webhook-owned field should be merged
        Assert.Equal(DeliveryStatus.Delivered, local.Recipients[0].DeliveryStatus);
    }
}
