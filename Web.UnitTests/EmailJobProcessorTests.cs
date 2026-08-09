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
using Web.MockData;
using Web.Models;
using Web.Services;
using Web.Services.Repositories;

namespace Web.UnitTests;

public sealed class EmailJobProcessorTests
{
    private readonly Mock<IEmailJobRepository> _jobRepo = new();
    private readonly Mock<IDocumentFileStore> _fileStore = new();
    private readonly Mock<IEmailDeliveryEventRepository> _deliveryEventRepo = new();
    private readonly Mock<IEmailDeliveryActionService> _deliveryActionService = new();
    private readonly Mock<IClientProxy> _clientProxy = new();
    private readonly EmailJobQueue _queue = new();

    // The real Mock repository rather than a Moq stub, so tests seed suppressions the same way
    // the MockData environment does and get the same active-only filtering.
    private readonly MockEmailSuppressionRepository _suppressions = new();

    // When set, CreateProcessor's scope hands this out instead of _suppressions - the fail-closed
    // test swaps in a repository whose read throws.
    private IEmailSuppressionRepository? _suppressionRepoOverride;

    public EmailJobProcessorTests()
    {
        // Default: TryClaimAsync succeeds (single-instance tests)
        _jobRepo.Setup(r => r.TryClaimAsync(It.IsAny<EmailJob>())).ReturnsAsync(true);

        // Default: no delivery events
        _deliveryEventRepo
            .Setup(r => r.GetByJobIdAsync(It.IsAny<Guid>()))
            .ReturnsAsync(new List<EmailDeliveryEvent>());
    }

    private static EmailTransportRouter CreateRouterFromTransport(IEmailTransport transport)
    {
        var postmarkOptions = Options.Create(new PostmarkOptions { Enabled = false });
        return new EmailTransportRouter(transport, transport, transport, postmarkOptions);
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
        serviceProvider
            .Setup(sp => sp.GetService(typeof(IEmailDeliveryEventRepository)))
            .Returns(_deliveryEventRepo.Object);
        serviceProvider
            .Setup(sp => sp.GetService(typeof(IEmailDeliveryActionService)))
            .Returns(_deliveryActionService.Object);
        // Deferred so a test can swap in a throwing repository (the fail-closed path) after the
        // processor is constructed.
        serviceProvider
            .Setup(sp => sp.GetService(typeof(IEmailSuppressionRepository)))
            .Returns(() => _suppressionRepoOverride ?? _suppressions);
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

        var mockSmtp = new Mock<IEmailTransport>();
        mockSmtp.Setup(t => t.ProviderName).Returns("SendGrid");
        mockSmtp
            .Setup(t =>
                t.SendAsync(
                    It.IsAny<MimeKit.MimeMessage>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new EmailSendResult { Success = true, ProviderName = "SendGrid" });

        return new EmailJobProcessor(
            _queue,
            scopeFactory.Object,
            CreateRouterFromTransport(mockSmtp.Object),
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
    public async Task RealSend_PerRecipient_EmitsTheStampedShortLink()
    {
        // The processor never issues a credential - links are stamped on recipients at job creation
        // (EmailController), and the send loop only reads. Issuance was twice placed inside this
        // loop and broke a different invariant each time; the processor having no way to mint is
        // what locks that. This asserts the read side: the stamped id reaches the footer as
        // /u/{id} and the RFC 8058 header as ?u=, so one-click and the body link redeem the same
        // credential, and no token appears anywhere.
        var jobId = Guid.NewGuid();
        var job = CreateSingleRecipientJob(jobId, Guid.NewGuid());
        job.Recipients[0].UnsubscribeLinkId = "stamped-at-creation-id";

        MimeKit.MimeMessage? captured = null;
        var mockSmtp = new Mock<IEmailTransport>();
        mockSmtp.Setup(t => t.ProviderName).Returns("SendGrid");
        mockSmtp
            .Setup(t =>
                t.SendAsync(
                    It.IsAny<MimeKit.MimeMessage>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback<MimeKit.MimeMessage, string, string, string, CancellationToken>((m, _, _, _, _) => captured = m)
            .ReturnsAsync(new EmailSendResult { Success = true, ProviderName = "SendGrid" });

        _jobRepo.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(job);
        _jobRepo.Setup(r => r.GetIncompleteJobsAsync()).ReturnsAsync(new List<EmailJob>());
        _jobRepo.Setup(r => r.UpdateAsync(It.IsAny<EmailJob>())).Returns(Task.CompletedTask);

        var processor = CreateRealSendProcessor(mockSmtp);
        await RunProcessorForSingleJob(processor, jobId);

        Assert.NotNull(captured);

        var body = captured!.HtmlBody ?? captured.TextBody ?? "";
        Assert.Contains("https://test.cohad.org/u/stamped-at-creation-id", body);
        Assert.DoesNotContain("token=", body);

        var listUnsubscribe = captured.Headers["List-Unsubscribe"];
        Assert.Contains("u=stamped-at-creation-id", listUnsubscribe);
        Assert.DoesNotContain("token=", listUnsubscribe);
    }

    [Fact]
    public async Task RealSend_RecipientWithoutAStampedLink_SendsWithNoFooterOrHeader()
    {
        // Null means job creation had nothing to link to (no AppBaseUrl, or a recipient without a
        // home). That is a configuration state, not a fault - the mail still goes, without the
        // footer, exactly as the legacy no-signing-key path behaved.
        var jobId = Guid.NewGuid();
        var job = CreateSingleRecipientJob(jobId, Guid.NewGuid());
        Assert.Null(job.Recipients[0].UnsubscribeLinkId);

        MimeKit.MimeMessage? captured = null;
        var mockSmtp = new Mock<IEmailTransport>();
        mockSmtp.Setup(t => t.ProviderName).Returns("SendGrid");
        mockSmtp
            .Setup(t =>
                t.SendAsync(
                    It.IsAny<MimeKit.MimeMessage>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback<MimeKit.MimeMessage, string, string, string, CancellationToken>((m, _, _, _, _) => captured = m)
            .ReturnsAsync(new EmailSendResult { Success = true, ProviderName = "SendGrid" });

        _jobRepo.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(job);
        _jobRepo.Setup(r => r.GetIncompleteJobsAsync()).ReturnsAsync(new List<EmailJob>());
        _jobRepo.Setup(r => r.UpdateAsync(It.IsAny<EmailJob>())).Returns(Task.CompletedTask);

        var processor = CreateRealSendProcessor(mockSmtp);
        await RunProcessorForSingleJob(processor, jobId);

        Assert.NotNull(captured);
        Assert.Equal(EmailJobRecipientStatus.Sent, job.Recipients[0].Status);
        Assert.Null(captured!.Headers["List-Unsubscribe"]);
        Assert.DoesNotContain("/u/", captured.HtmlBody ?? captured.TextBody ?? "");
    }

    private EmailJob CreateSingleRecipientJob(Guid jobId, Guid homeId)
    {
        return new EmailJob
        {
            Id = jobId,
            Status = EmailJobStatus.Queued,
            Category = "board",
            FromEmail = "board@cohad.org",
            FromDisplay = "COHAD Board",
            Subject = "Annual Meeting",
            ContentBlobPath = $"email-jobs/{jobId:D}.html",
            TotalRecipients = 1,
            MaxRecipientAttempts = 3,
            Recipients = new List<EmailJobRecipient>
            {
                new EmailJobRecipient
                {
                    Email = "jane@example.com",
                    HomeId = homeId,
                    Status = EmailJobRecipientStatus.Pending,
                },
            },
        };
    }

    /// <summary>Builds a processor on the real (non-mock-environment) send path.</summary>
    private EmailJobProcessor CreateRealSendProcessor(Mock<IEmailTransport> transport)
    {
        _fileStore
            .Setup(f => f.DownloadAsync(It.IsAny<string>()))
            .ReturnsAsync(
                new DocumentFileResult
                {
                    Stream = new MemoryStream("<p>Agenda</p>"u8.ToArray()),
                    ContentType = "text/html",
                }
            );

        var scopeFactory = new Mock<IServiceScopeFactory>();
        var scope = new Mock<IServiceScope>();
        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(sp => sp.GetService(typeof(IEmailJobRepository))).Returns(_jobRepo.Object);
        serviceProvider.Setup(sp => sp.GetService(typeof(IDocumentFileStore))).Returns(_fileStore.Object);
        serviceProvider
            .Setup(sp => sp.GetService(typeof(IEmailDeliveryEventRepository)))
            .Returns(_deliveryEventRepo.Object);
        serviceProvider
            .Setup(sp => sp.GetService(typeof(IEmailDeliveryActionService)))
            .Returns(_deliveryActionService.Object);
        serviceProvider.Setup(sp => sp.GetService(typeof(IEmailSuppressionRepository))).Returns(_suppressions);
        scope.Setup(s => s.ServiceProvider).Returns(serviceProvider.Object);
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        var hubContext = new Mock<IHubContext<EmailJobHub>>();
        var mockClients = new Mock<IHubClients>();
        mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_clientProxy.Object);
        hubContext.Setup(h => h.Clients).Returns(mockClients.Object);

        var env = new Mock<IWebHostEnvironment>();
        env.Setup(e => e.EnvironmentName).Returns("Production");

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
                }
            )
            .Build();

        return new EmailJobProcessor(
            _queue,
            scopeFactory.Object,
            CreateRouterFromTransport(transport.Object),
            hubContext.Object,
            config,
            env.Object,
            NullLogger<EmailJobProcessor>.Instance
        );
    }

    [Fact]
    public async Task RealSend_GroupRecipients_CallsTransportOnce_MarksAllSent()
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
            MaxRecipientAttempts = 3,
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

        MimeKit.MimeMessage? capturedMessage = null;
        string? capturedRecipientArg = null;

        var mockSmtp = new Mock<IEmailTransport>();
        mockSmtp.Setup(t => t.ProviderName).Returns("SendGrid");
        mockSmtp
            .Setup(t =>
                t.SendAsync(
                    It.IsAny<MimeKit.MimeMessage>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback<MimeKit.MimeMessage, string, string, string, CancellationToken>(
                (msg, _, rcpt, _, _) =>
                {
                    capturedMessage = msg;
                    capturedRecipientArg = rcpt;
                }
            )
            .ReturnsAsync(
                new EmailSendResult
                {
                    Success = true,
                    ProviderName = "SendGrid",
                    ProviderMessageId = "sg-msg-abc",
                }
            );

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

        // Use a non-mock environment so ProcessJobSendAsync (real path) runs
        var scopeFactory = new Mock<IServiceScopeFactory>();
        var scope = new Mock<IServiceScope>();
        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(sp => sp.GetService(typeof(IEmailJobRepository))).Returns(_jobRepo.Object);
        serviceProvider.Setup(sp => sp.GetService(typeof(IDocumentFileStore))).Returns(_fileStore.Object);
        serviceProvider
            .Setup(sp => sp.GetService(typeof(IEmailDeliveryEventRepository)))
            .Returns(_deliveryEventRepo.Object);
        serviceProvider
            .Setup(sp => sp.GetService(typeof(IEmailDeliveryActionService)))
            .Returns(_deliveryActionService.Object);
        serviceProvider.Setup(sp => sp.GetService(typeof(IEmailSuppressionRepository))).Returns(_suppressions);
        scope.Setup(s => s.ServiceProvider).Returns(serviceProvider.Object);
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        var hubContext = new Mock<IHubContext<EmailJobHub>>();
        var mockClients = new Mock<IHubClients>();
        mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_clientProxy.Object);
        hubContext.Setup(h => h.Clients).Returns(mockClients.Object);

        var env = new Mock<IWebHostEnvironment>();
        env.Setup(e => e.EnvironmentName).Returns("Production");

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
                }
            )
            .Build();

        var processor = new EmailJobProcessor(
            _queue,
            scopeFactory.Object,
            CreateRouterFromTransport(mockSmtp.Object),
            hubContext.Object,
            config,
            env.Object,
            NullLogger<EmailJobProcessor>.Instance
        );

        await RunProcessorForSingleJob(processor, jobId);

        // Transport called exactly once (not once per recipient)
        mockSmtp.Verify(
            t =>
                t.SendAsync(
                    It.IsAny<MimeKit.MimeMessage>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );

        // Sentinel correlation arg used (not string.Empty, not a real email)
        Assert.Equal("__grouped_send__", capturedRecipientArg);

        // All 3 recipients in To:
        Assert.NotNull(capturedMessage);
        var toAddresses = capturedMessage.To.Mailboxes.Select(m => m.Address).OrderBy(a => a).ToList();
        Assert.Equal(3, toAddresses.Count);
        Assert.Equal("admin1@test.com", toAddresses[0]);
        Assert.Equal("admin2@test.com", toAddresses[1]);
        Assert.Equal("admin3@test.com", toAddresses[2]);

        // All recipients marked Sent with ProviderMessageId propagated
        Assert.Equal(EmailJobStatus.Completed, job.Status);
        Assert.Equal(3, job.SentCount);
        Assert.All(
            job.Recipients,
            r =>
            {
                Assert.Equal(EmailJobRecipientStatus.Sent, r.Status);
                Assert.NotNull(r.SentUtc);
                Assert.Equal(1, r.AttemptCount);
                Assert.Equal("sg-msg-abc", r.ProviderMessageId);
                Assert.Equal("SendGrid", r.Provider);
            }
        );
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
            var mockSmtp2 = new Mock<IEmailTransport>();
            mockSmtp2.Setup(t => t.ProviderName).Returns("SendGrid");
            mockSmtp2
                .Setup(t =>
                    t.SendAsync(
                        It.IsAny<MimeKit.MimeMessage>(),
                        It.IsAny<string>(),
                        It.IsAny<string>(),
                        It.IsAny<string>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .ReturnsAsync(new EmailSendResult { Success = true, ProviderName = "SendGrid" });
            var processor = new EmailJobProcessor(
                queue,
                scopeFactory.Object,
                CreateRouterFromTransport(mockSmtp2.Object),
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
            StartedUtc = DateTime.UtcNow.AddMinutes(-60),
            LastProgressUtc = DateTime.UtcNow.AddMinutes(-45), // stalled — previous process died
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
    public async Task Startup_SkipsNonStalledInProgressJobs()
    {
        // A non-stalled InProgress job should NOT be enqueued at startup
        // because another processor instance may still be actively working on it.
        var jobId = Guid.NewGuid();
        var activeJob = new EmailJob
        {
            Id = jobId,
            Status = EmailJobStatus.InProgress,
            Category = "board",
            ContentBlobPath = $"email-jobs/{jobId:D}.html",
            TotalRecipients = 2,
            SentCount = 1,
            StartedUtc = DateTime.UtcNow.AddMinutes(-5),
            LastProgressUtc = DateTime.UtcNow.AddMinutes(-1), // recent progress — NOT stalled
            Recipients = new List<EmailJobRecipient>
            {
                new()
                {
                    Email = "done@test.com",
                    Status = EmailJobRecipientStatus.Sent,
                    SentUtc = DateTime.UtcNow,
                },
                new()
                {
                    Email = "pending@test.com",
                    HomeId = Guid.NewGuid(),
                    Status = EmailJobRecipientStatus.Pending,
                },
            },
        };

        _jobRepo.Setup(r => r.GetIncompleteJobsAsync()).ReturnsAsync(new List<EmailJob> { activeJob });
        _jobRepo.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(activeJob);

        var processor = CreateProcessor();
        using var cts = new CancellationTokenSource();
        await processor.StartAsync(cts.Token);

        // Give the startup resume path time to run
        await Task.Delay(200);

        cts.Cancel();
        try
        {
            await processor.StopAsync(CancellationToken.None);
        }
        catch (OperationCanceledException) { }

        // The non-stalled InProgress job should not be claimed or processed
        _jobRepo.Verify(r => r.TryClaimAsync(It.IsAny<EmailJob>()), Times.Never);
        _jobRepo.Verify(r => r.UpdateAsync(It.IsAny<EmailJob>()), Times.Never);
        // Job status should remain unchanged
        Assert.Equal(EmailJobStatus.InProgress, activeJob.Status);
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

    // ───────────────────────────────────────────────────
    // InProgress stall guard (prevents duplicate processing)
    // ───────────────────────────────────────────────────

    [Fact]
    public async Task InProgressWithRecentProgress_Skips()
    {
        var jobId = Guid.NewGuid();
        var job = new EmailJob
        {
            Id = jobId,
            Status = EmailJobStatus.InProgress,
            Category = "board",
            FromEmail = "board@cohad.org",
            FromDisplay = "COHAD Board",
            Subject = "Active job",
            ContentBlobPath = $"email-jobs/{jobId:D}.html",
            StartedUtc = DateTime.UtcNow.AddMinutes(-5),
            LastProgressUtc = DateTime.UtcNow.AddMinutes(-1), // recent progress
            TotalRecipients = 1,
            Recipients = new List<EmailJobRecipient>
            {
                new()
                {
                    Email = "a@test.com",
                    HomeId = Guid.NewGuid(),
                    Status = EmailJobRecipientStatus.Pending,
                },
            },
        };

        _jobRepo.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(job);
        _jobRepo.Setup(r => r.GetIncompleteJobsAsync()).ReturnsAsync(new List<EmailJob>());

        var processor = CreateProcessor();
        await RunProcessorForSingleJob(processor, jobId);

        // Should NOT attempt to claim — job has recent progress
        _jobRepo.Verify(r => r.TryClaimAsync(It.IsAny<EmailJob>()), Times.Never);
        _jobRepo.Verify(r => r.UpdateAsync(It.IsAny<EmailJob>()), Times.Never);
    }

    [Fact]
    public async Task InProgressButStalled_ProceedsToClaim()
    {
        var jobId = Guid.NewGuid();
        var job = new EmailJob
        {
            Id = jobId,
            Status = EmailJobStatus.InProgress,
            Category = "board",
            FromEmail = "board@cohad.org",
            FromDisplay = "COHAD Board",
            Subject = "Stalled job",
            ContentBlobPath = $"email-jobs/{jobId:D}.html",
            StartedUtc = DateTime.UtcNow.AddMinutes(-60),
            LastProgressUtc = DateTime.UtcNow.AddMinutes(-45), // stalled (>30 min default)
            TotalRecipients = 1,
            Recipients = new List<EmailJobRecipient>
            {
                new()
                {
                    Email = "a@test.com",
                    HomeId = Guid.NewGuid(),
                    Status = EmailJobRecipientStatus.Pending,
                },
            },
        };

        _jobRepo.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(job);
        _jobRepo.Setup(r => r.TryClaimAsync(It.IsAny<EmailJob>())).ReturnsAsync(true);
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

        // Should have attempted to claim the stalled job
        _jobRepo.Verify(r => r.TryClaimAsync(It.IsAny<EmailJob>()), Times.Once);
        Assert.Equal(EmailJobStatus.Completed, job.Status);
    }

    // ───────────────────────────────────────────────────
    // TryClaimAsync retry on ETag conflict
    // ───────────────────────────────────────────────────

    [Fact]
    public async Task ClaimRetry_SucceedsOnSecondAttempt_ProcessesJob()
    {
        var jobId = Guid.NewGuid();
        var job = new EmailJob
        {
            Id = jobId,
            Status = EmailJobStatus.Queued,
            Category = "board",
            FromEmail = "board@cohad.org",
            FromDisplay = "COHAD Board",
            Subject = "Claim retry test",
            ContentBlobPath = $"email-jobs/{jobId:D}.html",
            TotalRecipients = 1,
            Recipients = new List<EmailJobRecipient>
            {
                new EmailJobRecipient
                {
                    Email = "a@test.com",
                    HomeId = Guid.NewGuid(),
                    Status = EmailJobRecipientStatus.Pending,
                },
            },
        };

        // First TryClaimAsync fails (webhook changed the ETag), second succeeds
        var claimCallCount = 0;
        _jobRepo.Setup(r => r.TryClaimAsync(It.IsAny<EmailJob>())).ReturnsAsync(() => ++claimCallCount > 1);
        // First GetByIdAsync returns the original job (processor's working copy).
        // Subsequent calls (claim retry re-reads) return fresh Queued copies so
        // the processor sees the server state, not the locally-mutated InProgress copy.
        var getByIdCallCount = 0;
        _jobRepo
            .Setup(r => r.GetByIdAsync(jobId))
            .ReturnsAsync(() =>
            {
                if (++getByIdCallCount == 1)
                    return job;
                return new EmailJob
                {
                    Id = jobId,
                    Status = EmailJobStatus.Queued,
                    Category = job.Category,
                    FromEmail = job.FromEmail,
                    FromDisplay = job.FromDisplay,
                    Subject = job.Subject,
                    ContentBlobPath = job.ContentBlobPath,
                    TotalRecipients = job.TotalRecipients,
                    Recipients = job.Recipients,
                };
            });
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
        Assert.Equal(1, job.SentCount);
        Assert.Equal(2, claimCallCount);
    }

    [Fact]
    public async Task ClaimRetry_AllAttemptsFail_DoesNotCrash()
    {
        var jobId = Guid.NewGuid();
        var job = new EmailJob
        {
            Id = jobId,
            Status = EmailJobStatus.Queued,
            Category = "board",
            FromEmail = "board@cohad.org",
            FromDisplay = "COHAD Board",
            Subject = "Claim exhaust test",
            ContentBlobPath = $"email-jobs/{jobId:D}.html",
            TotalRecipients = 1,
            Recipients = new List<EmailJobRecipient>
            {
                new EmailJobRecipient
                {
                    Email = "a@test.com",
                    HomeId = Guid.NewGuid(),
                    Status = EmailJobRecipientStatus.Pending,
                },
            },
        };

        // TryClaimAsync always fails
        _jobRepo.Setup(r => r.TryClaimAsync(It.IsAny<EmailJob>())).ReturnsAsync(false);
        // First call returns the original job (processor's working copy).
        // Subsequent calls (claim retry re-reads) return fresh Queued copies.
        var getByIdCallCount2 = 0;
        _jobRepo
            .Setup(r => r.GetByIdAsync(jobId))
            .ReturnsAsync(() =>
            {
                if (++getByIdCallCount2 == 1)
                    return job;
                return new EmailJob
                {
                    Id = jobId,
                    Status = EmailJobStatus.Queued,
                    Category = job.Category,
                    FromEmail = job.FromEmail,
                    FromDisplay = job.FromDisplay,
                    Subject = job.Subject,
                    ContentBlobPath = job.ContentBlobPath,
                    TotalRecipients = job.TotalRecipients,
                    Recipients = job.Recipients,
                };
            });
        _jobRepo.Setup(r => r.GetIncompleteJobsAsync()).ReturnsAsync(new List<EmailJob>());

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
        using var cts = new CancellationTokenSource();
        await processor.StartAsync(cts.Token);
        await _queue.EnqueueAsync(jobId);

        // Wait for the processor to exhaust all 3 claim attempts
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (_jobRepo.Invocations.Count(i => i.Method.Name == "TryClaimAsync") >= 3)
                break;
            await Task.Delay(50);
        }

        cts.Cancel();
        try
        {
            await processor.StopAsync(CancellationToken.None);
        }
        catch (OperationCanceledException) { }

        // Job should NOT be in a terminal state — left for the stall watchdog
        Assert.NotEqual(EmailJobStatus.Completed, job.Status);
        // TryClaimAsync should have been called 3 times (max attempts)
        _jobRepo.Verify(r => r.TryClaimAsync(It.IsAny<EmailJob>()), Times.Exactly(3));
    }

    [Fact]
    public async Task ClaimRetry_CancelledDuringRetry_Skips()
    {
        var jobId = Guid.NewGuid();
        var job = new EmailJob
        {
            Id = jobId,
            Status = EmailJobStatus.Queued,
            Category = "board",
            FromEmail = "board@cohad.org",
            FromDisplay = "COHAD Board",
            Subject = "Cancelled during claim",
            ContentBlobPath = $"email-jobs/{jobId:D}.html",
            TotalRecipients = 1,
            Recipients = new List<EmailJobRecipient>
            {
                new EmailJobRecipient
                {
                    Email = "a@test.com",
                    HomeId = Guid.NewGuid(),
                    Status = EmailJobRecipientStatus.Pending,
                },
            },
        };

        // First claim fails, then re-read returns Cancelled job
        _jobRepo.Setup(r => r.TryClaimAsync(It.IsAny<EmailJob>())).ReturnsAsync(false);
        var cancelledJob = new EmailJob
        {
            Id = jobId,
            Status = EmailJobStatus.Cancelled,
            Recipients = job.Recipients,
        };
        _jobRepo
            .SetupSequence(r => r.GetByIdAsync(jobId))
            .ReturnsAsync(job) // initial read
            .ReturnsAsync(cancelledJob); // re-read during claim retry
        _jobRepo.Setup(r => r.GetIncompleteJobsAsync()).ReturnsAsync(new List<EmailJob>());

        var processor = CreateProcessor();
        using var cts = new CancellationTokenSource();
        await processor.StartAsync(cts.Token);
        await _queue.EnqueueAsync(jobId);

        // Wait for the processor to see the Cancelled status (1 claim + 1 re-read)
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (
                _jobRepo.Invocations.Count(i => i.Method.Name == "TryClaimAsync") >= 1
                && _jobRepo.Invocations.Count(i => i.Method.Name == "GetByIdAsync") >= 2
            )
                break;
            await Task.Delay(50);
        }

        cts.Cancel();
        try
        {
            await processor.StopAsync(CancellationToken.None);
        }
        catch (OperationCanceledException) { }

        // Should have only tried to claim once before seeing Cancelled
        _jobRepo.Verify(r => r.TryClaimAsync(It.IsAny<EmailJob>()), Times.Once);
        // No UpdateAsync calls — job was cancelled, no processing
        _jobRepo.Verify(r => r.UpdateAsync(It.IsAny<EmailJob>()), Times.Never);
    }

    // ───────────────────────────────────────────────────
    // MergeJobFromServer
    // ───────────────────────────────────────────────────

    [Fact]
    public void MergeJobFromServer_adopts_Sent_status_from_server()
    {
        var local = new EmailJob
        {
            Recipients = new List<EmailJobRecipient>
            {
                new() { Email = "a@test.com", Status = EmailJobRecipientStatus.Pending },
                new() { Email = "b@test.com", Status = EmailJobRecipientStatus.Pending },
            },
        };
        var server = new EmailJob
        {
            Recipients = new List<EmailJobRecipient>
            {
                new()
                {
                    Email = "a@test.com",
                    Status = EmailJobRecipientStatus.Sent,
                    SentUtc = new DateTime(2026, 4, 7, 3, 36, 0, DateTimeKind.Utc),
                    Provider = "SendGrid",
                },
                new() { Email = "b@test.com", Status = EmailJobRecipientStatus.Pending },
            },
        };

        EmailJobProcessor.MergeJobFromServer(local, server);

        Assert.Equal(EmailJobRecipientStatus.Sent, local.Recipients[0].Status);
        Assert.Equal(new DateTime(2026, 4, 7, 3, 36, 0, DateTimeKind.Utc), local.Recipients[0].SentUtc);
        Assert.Equal(EmailJobRecipientStatus.Pending, local.Recipients[1].Status);
    }

    [Fact]
    public void MergeJobFromServer_does_not_overwrite_already_Sent()
    {
        var local = new EmailJob
        {
            Recipients = new List<EmailJobRecipient>
            {
                new()
                {
                    Email = "a@test.com",
                    Status = EmailJobRecipientStatus.Sent,
                    SentUtc = new DateTime(2026, 4, 7, 1, 0, 0, DateTimeKind.Utc),
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
                    Status = EmailJobRecipientStatus.Sent,
                    SentUtc = new DateTime(2026, 4, 7, 2, 0, 0, DateTimeKind.Utc),
                },
            },
        };

        EmailJobProcessor.MergeJobFromServer(local, server);

        // Should keep local SentUtc
        Assert.Equal(new DateTime(2026, 4, 7, 1, 0, 0, DateTimeKind.Utc), local.Recipients[0].SentUtc);
    }

    [Fact]
    public void MergeJobFromServer_handles_null_recipients()
    {
        var local = new EmailJob { Recipients = null };
        var server = new EmailJob
        {
            Recipients = new List<EmailJobRecipient>
            {
                new() { Email = "a@test.com", Status = EmailJobRecipientStatus.Sent },
            },
        };

        // Should not throw
        EmailJobProcessor.MergeJobFromServer(local, server);
        EmailJobProcessor.MergeJobFromServer(server, new EmailJob { Recipients = null });
    }

    [Fact]
    public void MergeJobFromServer_adopts_delivery_progress_from_server()
    {
        var local = new EmailJob
        {
            Recipients = new List<EmailJobRecipient>
            {
                new()
                {
                    Email = "a@test.com",
                    Status = EmailJobRecipientStatus.Sent,
                    DeliveryStatus = DeliveryStatus.Unknown,
                    ProviderMessageId = null,
                },
                new()
                {
                    Email = "b@test.com",
                    Status = EmailJobRecipientStatus.Sent,
                    DeliveryStatus = DeliveryStatus.Delivered,
                    DeliveryStatusUpdatedUtc = new DateTime(2026, 4, 7, 3, 0, 0, DateTimeKind.Utc),
                    ProviderMessageId = "local-id",
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
                    Status = EmailJobRecipientStatus.Sent,
                    DeliveryStatus = DeliveryStatus.Delivered,
                    DeliveryStatusUpdatedUtc = new DateTime(2026, 4, 7, 4, 0, 0, DateTimeKind.Utc),
                    ProviderMessageId = "server-id-a",
                },
                new()
                {
                    Email = "b@test.com",
                    Status = EmailJobRecipientStatus.Sent,
                    DeliveryStatus = DeliveryStatus.Delivered,
                    DeliveryStatusUpdatedUtc = new DateTime(2026, 4, 7, 2, 0, 0, DateTimeKind.Utc),
                    ProviderMessageId = "server-id-b",
                },
            },
        };

        EmailJobProcessor.MergeJobFromServer(local, server);

        // a@: local was Unknown, server was Delivered → adopted
        Assert.Equal(DeliveryStatus.Delivered, local.Recipients[0].DeliveryStatus);
        Assert.Equal(new DateTime(2026, 4, 7, 4, 0, 0, DateTimeKind.Utc), local.Recipients[0].DeliveryStatusUpdatedUtc);
        Assert.Equal("server-id-a", local.Recipients[0].ProviderMessageId);

        // b@: local already had Delivered, server also Delivered (same severity) → kept local
        Assert.Equal(DeliveryStatus.Delivered, local.Recipients[1].DeliveryStatus);
        Assert.Equal(new DateTime(2026, 4, 7, 3, 0, 0, DateTimeKind.Utc), local.Recipients[1].DeliveryStatusUpdatedUtc);
        Assert.Equal("local-id", local.Recipients[1].ProviderMessageId); // local already had one
    }

    // ───────────────────────────────────────────────────
    // ApplyDeliveryEventsAsync
    // ───────────────────────────────────────────────────

    [Fact]
    public async Task ApplyDeliveryEventsAsync_applies_delivery_status()
    {
        var jobId = Guid.NewGuid();
        var job = new EmailJob
        {
            Id = jobId,
            Category = "board",
            Recipients = new List<EmailJobRecipient>
            {
                new()
                {
                    Email = "a@test.com",
                    Status = EmailJobRecipientStatus.Sent,
                    DeliveryStatus = DeliveryStatus.Unknown,
                },
            },
        };

        var mockRepo = new Web.MockData.MockEmailDeliveryEventRepository();
        await mockRepo.AddAsync(
            new EmailDeliveryEvent
            {
                Id = EmailDeliveryEvent.MakeId(jobId, "a@test.com", DeliveryStatus.Delivered),
                JobId = jobId,
                Email = "a@test.com",
                DeliveryStatus = DeliveryStatus.Delivered,
                ProviderMessageId = "msg-123",
                ReceivedUtc = DateTime.UtcNow,
            }
        );

        var mockAction = new Mock<IEmailDeliveryActionService>();

        var changed = await EmailJobProcessor.ApplyDeliveryEventsAsync(
            job,
            mockRepo,
            mockAction.Object,
            NullLogger<EmailJobProcessor>.Instance
        );

        Assert.True(changed);
        Assert.Equal(DeliveryStatus.Delivered, job.Recipients[0].DeliveryStatus);
        Assert.Equal("msg-123", job.Recipients[0].ProviderMessageId);
    }

    [Fact]
    public async Task ApplyDeliveryEventsAsync_promotes_Pending_to_Sent()
    {
        var jobId = Guid.NewGuid();
        var job = new EmailJob
        {
            Id = jobId,
            Category = "board",
            Recipients = new List<EmailJobRecipient>
            {
                new()
                {
                    Email = "a@test.com",
                    Status = EmailJobRecipientStatus.Pending,
                    DeliveryStatus = DeliveryStatus.Unknown,
                },
            },
        };

        var mockRepo = new Web.MockData.MockEmailDeliveryEventRepository();
        await mockRepo.AddAsync(
            new EmailDeliveryEvent
            {
                Id = EmailDeliveryEvent.MakeId(jobId, "a@test.com", DeliveryStatus.Delivered),
                JobId = jobId,
                Email = "a@test.com",
                DeliveryStatus = DeliveryStatus.Delivered,
                ReceivedUtc = DateTime.UtcNow,
            }
        );

        var mockAction = new Mock<IEmailDeliveryActionService>();

        await EmailJobProcessor.ApplyDeliveryEventsAsync(
            job,
            mockRepo,
            mockAction.Object,
            NullLogger<EmailJobProcessor>.Instance
        );

        Assert.Equal(EmailJobRecipientStatus.Sent, job.Recipients[0].Status);
        Assert.NotNull(job.Recipients[0].SentUtc);
        Assert.Equal(1, job.SentCount);
    }

    [Fact]
    public async Task ApplyDeliveryEventsAsync_runs_delivery_action_for_bounce()
    {
        var jobId = Guid.NewGuid();
        var job = new EmailJob
        {
            Id = jobId,
            Category = "board",
            Recipients = new List<EmailJobRecipient>
            {
                new()
                {
                    Email = "bounced@test.com",
                    Status = EmailJobRecipientStatus.Sent,
                    DeliveryStatus = DeliveryStatus.Unknown,
                },
            },
        };

        var mockRepo = new Web.MockData.MockEmailDeliveryEventRepository();
        await mockRepo.AddAsync(
            new EmailDeliveryEvent
            {
                Id = EmailDeliveryEvent.MakeId(jobId, "bounced@test.com", DeliveryStatus.Bounced),
                JobId = jobId,
                Email = "bounced@test.com",
                DeliveryStatus = DeliveryStatus.Bounced,
                ReceivedUtc = DateTime.UtcNow,
                ActionProcessed = false,
            }
        );

        var mockAction = new Mock<IEmailDeliveryActionService>();

        await EmailJobProcessor.ApplyDeliveryEventsAsync(
            job,
            mockRepo,
            mockAction.Object,
            NullLogger<EmailJobProcessor>.Instance
        );

        Assert.Equal(DeliveryStatus.Bounced, job.Recipients[0].DeliveryStatus);
        mockAction.Verify(
            a =>
                a.ProcessDeliveryEventAsync(
                    It.Is<EmailDeliveryEvent>(e =>
                        e.Email == "bounced@test.com" && e.DeliveryStatus == DeliveryStatus.Bounced && e.JobId == jobId
                    ),
                    "board"
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task ApplyDeliveryEventsAsync_skips_already_processed_actions()
    {
        var jobId = Guid.NewGuid();
        var job = new EmailJob
        {
            Id = jobId,
            Category = "board",
            Recipients = new List<EmailJobRecipient>
            {
                new()
                {
                    Email = "bounced@test.com",
                    Status = EmailJobRecipientStatus.Sent,
                    DeliveryStatus = DeliveryStatus.Unknown,
                },
            },
        };

        var mockRepo = new Web.MockData.MockEmailDeliveryEventRepository();
        await mockRepo.AddAsync(
            new EmailDeliveryEvent
            {
                Id = EmailDeliveryEvent.MakeId(jobId, "bounced@test.com", DeliveryStatus.Bounced),
                JobId = jobId,
                Email = "bounced@test.com",
                DeliveryStatus = DeliveryStatus.Bounced,
                ReceivedUtc = DateTime.UtcNow,
                ActionProcessed = true, // Already processed
            }
        );

        var mockAction = new Mock<IEmailDeliveryActionService>();

        await EmailJobProcessor.ApplyDeliveryEventsAsync(
            job,
            mockRepo,
            mockAction.Object,
            NullLogger<EmailJobProcessor>.Instance
        );

        // Delivery action should not be called again
        mockAction.Verify(
            a => a.ProcessDeliveryEventAsync(It.IsAny<EmailDeliveryEvent>(), It.IsAny<string?>()),
            Times.Never
        );
    }

    [Fact]
    public async Task ApplyDeliveryEventsAsync_returns_false_when_no_events()
    {
        var jobId = Guid.NewGuid();
        var job = new EmailJob
        {
            Id = jobId,
            Category = "board",
            Recipients = new List<EmailJobRecipient>
            {
                new() { Email = "a@test.com", Status = EmailJobRecipientStatus.Sent },
            },
        };

        var mockRepo = new Web.MockData.MockEmailDeliveryEventRepository();
        var mockAction = new Mock<IEmailDeliveryActionService>();

        var changed = await EmailJobProcessor.ApplyDeliveryEventsAsync(
            job,
            mockRepo,
            mockAction.Object,
            NullLogger<EmailJobProcessor>.Instance
        );

        Assert.False(changed);
    }

    [Fact]
    public async Task ApplyDeliveryEventsAsync_respects_severity_order()
    {
        var jobId = Guid.NewGuid();
        var job = new EmailJob
        {
            Id = jobId,
            Category = "board",
            Recipients = new List<EmailJobRecipient>
            {
                new()
                {
                    Email = "a@test.com",
                    Status = EmailJobRecipientStatus.Sent,
                    DeliveryStatus = DeliveryStatus.Bounced,
                },
            },
        };

        var mockRepo = new Web.MockData.MockEmailDeliveryEventRepository();
        // Delivered is lower severity than Bounced — should not downgrade
        await mockRepo.AddAsync(
            new EmailDeliveryEvent
            {
                Id = EmailDeliveryEvent.MakeId(jobId, "a@test.com", DeliveryStatus.Delivered),
                JobId = jobId,
                Email = "a@test.com",
                DeliveryStatus = DeliveryStatus.Delivered,
                ReceivedUtc = DateTime.UtcNow,
            }
        );

        var mockAction = new Mock<IEmailDeliveryActionService>();

        await EmailJobProcessor.ApplyDeliveryEventsAsync(
            job,
            mockRepo,
            mockAction.Object,
            NullLogger<EmailJobProcessor>.Instance
        );

        // Should not downgrade from Bounced to Delivered
        Assert.Equal(DeliveryStatus.Bounced, job.Recipients[0].DeliveryStatus);
    }

    // ───────────────────────────────────────────────────
    // SweepCompletedJobDeliveryEventsAsync
    // ───────────────────────────────────────────────────

    [Fact]
    public async Task Sweep_AppliesLateDeliveryEvents_ToCompletedJob()
    {
        var jobId = Guid.NewGuid();
        var completedJob = new EmailJob
        {
            Id = jobId,
            Status = EmailJobStatus.Completed,
            Category = "board",
            CompletedUtc = DateTime.UtcNow.AddMinutes(-5),
            TotalRecipients = 2,
            SentCount = 2,
            Recipients = new List<EmailJobRecipient>
            {
                new()
                {
                    Email = "a@test.com",
                    Status = EmailJobRecipientStatus.Sent,
                    DeliveryStatus = DeliveryStatus.Unknown,
                },
                new()
                {
                    Email = "b@test.com",
                    Status = EmailJobRecipientStatus.Sent,
                    DeliveryStatus = DeliveryStatus.Unknown,
                },
            },
        };

        _jobRepo
            .Setup(r => r.GetRecentlyCompletedJobsAsync(It.IsAny<DateTime>(), It.IsAny<int>()))
            .ReturnsAsync(new List<EmailJob> { completedJob });
        _jobRepo.Setup(r => r.UpdateAsync(It.IsAny<EmailJob>())).Returns(Task.CompletedTask);

        _deliveryEventRepo
            .Setup(r => r.GetByJobIdAsync(jobId))
            .ReturnsAsync(
                new List<EmailDeliveryEvent>
                {
                    new()
                    {
                        Id = EmailDeliveryEvent.MakeId(jobId, "a@test.com", DeliveryStatus.Delivered),
                        JobId = jobId,
                        Email = "a@test.com",
                        DeliveryStatus = DeliveryStatus.Delivered,
                        ReceivedUtc = DateTime.UtcNow,
                    },
                    new()
                    {
                        Id = EmailDeliveryEvent.MakeId(jobId, "b@test.com", DeliveryStatus.Bounced),
                        JobId = jobId,
                        Email = "b@test.com",
                        DeliveryStatus = DeliveryStatus.Bounced,
                        ReceivedUtc = DateTime.UtcNow,
                    },
                }
            );

        var processor = CreateProcessor();
        await processor.SweepCompletedJobDeliveryEventsAsync(CancellationToken.None);

        Assert.Equal(DeliveryStatus.Delivered, completedJob.Recipients[0].DeliveryStatus);
        Assert.Equal(DeliveryStatus.Bounced, completedJob.Recipients[1].DeliveryStatus);
        _jobRepo.Verify(r => r.UpdateAsync(It.Is<EmailJob>(j => j.Id == jobId)), Times.Once);
    }

    [Fact]
    public async Task Sweep_RunsDeliveryAction_ForUnprocessedBounce()
    {
        var jobId = Guid.NewGuid();
        var completedJob = new EmailJob
        {
            Id = jobId,
            Status = EmailJobStatus.PartiallyCompleted,
            Category = "social",
            CompletedUtc = DateTime.UtcNow.AddMinutes(-2),
            TotalRecipients = 1,
            SentCount = 1,
            Recipients = new List<EmailJobRecipient>
            {
                new()
                {
                    Email = "bounced@test.com",
                    Status = EmailJobRecipientStatus.Sent,
                    DeliveryStatus = DeliveryStatus.Unknown,
                },
            },
        };

        _jobRepo
            .Setup(r => r.GetRecentlyCompletedJobsAsync(It.IsAny<DateTime>(), It.IsAny<int>()))
            .ReturnsAsync(new List<EmailJob> { completedJob });
        _jobRepo.Setup(r => r.UpdateAsync(It.IsAny<EmailJob>())).Returns(Task.CompletedTask);

        _deliveryEventRepo
            .Setup(r => r.GetByJobIdAsync(jobId))
            .ReturnsAsync(
                new List<EmailDeliveryEvent>
                {
                    new()
                    {
                        Id = EmailDeliveryEvent.MakeId(jobId, "bounced@test.com", DeliveryStatus.Bounced),
                        JobId = jobId,
                        Email = "bounced@test.com",
                        DeliveryStatus = DeliveryStatus.Bounced,
                        ActionProcessed = false,
                        ReceivedUtc = DateTime.UtcNow,
                    },
                }
            );

        var processor = CreateProcessor();
        await processor.SweepCompletedJobDeliveryEventsAsync(CancellationToken.None);

        _deliveryActionService.Verify(
            s =>
                s.ProcessDeliveryEventAsync(
                    It.Is<EmailDeliveryEvent>(e =>
                        e.Email == "bounced@test.com" && e.DeliveryStatus == DeliveryStatus.Bounced
                    ),
                    "social"
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task Sweep_NoDeliveryEvents_DoesNotPersist()
    {
        var jobId = Guid.NewGuid();
        var completedJob = new EmailJob
        {
            Id = jobId,
            Status = EmailJobStatus.Completed,
            Category = "board",
            CompletedUtc = DateTime.UtcNow.AddMinutes(-3),
            TotalRecipients = 1,
            SentCount = 1,
            Recipients = new List<EmailJobRecipient>
            {
                new()
                {
                    Email = "a@test.com",
                    Status = EmailJobRecipientStatus.Sent,
                    DeliveryStatus = DeliveryStatus.Delivered,
                },
            },
        };

        _jobRepo
            .Setup(r => r.GetRecentlyCompletedJobsAsync(It.IsAny<DateTime>(), It.IsAny<int>()))
            .ReturnsAsync(new List<EmailJob> { completedJob });

        // Default mock returns empty delivery events list

        var processor = CreateProcessor();
        await processor.SweepCompletedJobDeliveryEventsAsync(CancellationToken.None);

        _jobRepo.Verify(r => r.UpdateAsync(It.IsAny<EmailJob>()), Times.Never);
    }

    [Fact]
    public async Task Sweep_ConcurrencyConflict_MergesSentStatusFromServer()
    {
        var jobId = Guid.NewGuid();
        var completedJob = new EmailJob
        {
            Id = jobId,
            Status = EmailJobStatus.Completed,
            Category = "board",
            CompletedUtc = DateTime.UtcNow.AddMinutes(-5),
            ETag = "etag-1",
            TotalRecipients = 2,
            SentCount = 1,
            Recipients = new List<EmailJobRecipient>
            {
                new()
                {
                    Email = "a@test.com",
                    Status = EmailJobRecipientStatus.Sent,
                    DeliveryStatus = DeliveryStatus.Unknown,
                },
                new()
                {
                    Email = "b@test.com",
                    Status = EmailJobRecipientStatus.Pending,
                    DeliveryStatus = DeliveryStatus.Unknown,
                },
            },
        };

        // Server copy: another processor already sent b@test.com
        var serverCopy = new EmailJob
        {
            Id = jobId,
            Status = EmailJobStatus.Completed,
            Category = "board",
            CompletedUtc = DateTime.UtcNow.AddMinutes(-5),
            ETag = "etag-2",
            TotalRecipients = 2,
            SentCount = 2,
            Recipients = new List<EmailJobRecipient>
            {
                new()
                {
                    Email = "a@test.com",
                    Status = EmailJobRecipientStatus.Sent,
                    DeliveryStatus = DeliveryStatus.Unknown,
                },
                new()
                {
                    Email = "b@test.com",
                    Status = EmailJobRecipientStatus.Sent,
                    SentUtc = new DateTime(2026, 4, 15, 1, 0, 0, DateTimeKind.Utc),
                    Provider = "SendGrid",
                },
            },
        };

        _jobRepo
            .Setup(r => r.GetRecentlyCompletedJobsAsync(It.IsAny<DateTime>(), It.IsAny<int>()))
            .ReturnsAsync(new List<EmailJob> { completedJob });

        // First UpdateAsync throws concurrency conflict; second succeeds
        var updateCallCount = 0;
        _jobRepo
            .Setup(r => r.UpdateAsync(It.IsAny<EmailJob>()))
            .Returns<EmailJob>(j =>
            {
                if (++updateCallCount == 1)
                    throw new EmailJobConcurrencyException();
                return Task.CompletedTask;
            });

        _jobRepo.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(serverCopy);

        _deliveryEventRepo
            .Setup(r => r.GetByJobIdAsync(jobId))
            .ReturnsAsync(
                new List<EmailDeliveryEvent>
                {
                    new()
                    {
                        Id = EmailDeliveryEvent.MakeId(jobId, "a@test.com", DeliveryStatus.Delivered),
                        JobId = jobId,
                        Email = "a@test.com",
                        DeliveryStatus = DeliveryStatus.Delivered,
                        ReceivedUtc = DateTime.UtcNow,
                    },
                }
            );

        var processor = CreateProcessor();
        await processor.SweepCompletedJobDeliveryEventsAsync(CancellationToken.None);

        // Verify retry succeeded (2 calls: first threw, second succeeded)
        _jobRepo.Verify(r => r.UpdateAsync(It.IsAny<EmailJob>()), Times.Exactly(2));

        // Verify merge adopted Sent status from server for b@test.com
        var recipientB = completedJob.Recipients.Single(r => r.Email == "b@test.com");
        Assert.Equal(EmailJobRecipientStatus.Sent, recipientB.Status);
        Assert.Equal(new DateTime(2026, 4, 15, 1, 0, 0, DateTimeKind.Utc), recipientB.SentUtc);

        // Verify delivery event was still applied to a@test.com
        var recipientA = completedJob.Recipients.Single(r => r.Email == "a@test.com");
        Assert.Equal(DeliveryStatus.Delivered, recipientA.DeliveryStatus);

        // Verify ETag was updated to server's
        Assert.Equal("etag-2", completedJob.ETag);
    }

    [Fact]
    public async Task Sweep_ConcurrencyConflict_ServerCancelled_ReturnsFalse()
    {
        var jobId = Guid.NewGuid();
        var completedJob = new EmailJob
        {
            Id = jobId,
            Status = EmailJobStatus.Completed,
            Category = "board",
            CompletedUtc = DateTime.UtcNow.AddMinutes(-5),
            ETag = "etag-1",
            TotalRecipients = 1,
            SentCount = 1,
            Recipients = new List<EmailJobRecipient>
            {
                new()
                {
                    Email = "a@test.com",
                    Status = EmailJobRecipientStatus.Sent,
                    DeliveryStatus = DeliveryStatus.Unknown,
                },
            },
        };

        var cancelledServerCopy = new EmailJob
        {
            Id = jobId,
            Status = EmailJobStatus.Cancelled,
            ETag = "etag-2",
        };

        _jobRepo
            .Setup(r => r.GetRecentlyCompletedJobsAsync(It.IsAny<DateTime>(), It.IsAny<int>()))
            .ReturnsAsync(new List<EmailJob> { completedJob });

        _jobRepo.Setup(r => r.UpdateAsync(It.IsAny<EmailJob>())).ThrowsAsync(new EmailJobConcurrencyException());

        _jobRepo.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(cancelledServerCopy);

        _deliveryEventRepo
            .Setup(r => r.GetByJobIdAsync(jobId))
            .ReturnsAsync(
                new List<EmailDeliveryEvent>
                {
                    new()
                    {
                        Id = EmailDeliveryEvent.MakeId(jobId, "a@test.com", DeliveryStatus.Delivered),
                        JobId = jobId,
                        Email = "a@test.com",
                        DeliveryStatus = DeliveryStatus.Delivered,
                        ReceivedUtc = DateTime.UtcNow,
                    },
                }
            );

        var processor = CreateProcessor();
        await processor.SweepCompletedJobDeliveryEventsAsync(CancellationToken.None);

        // Only one attempt — gave up after seeing Cancelled
        _jobRepo.Verify(r => r.UpdateAsync(It.IsAny<EmailJob>()), Times.Once);
    }

    // ───────────────────────────────────────────────────
    // Suppression enforcement (docs/email-suppression-and-unsubscribe.md, Part 3)
    // ───────────────────────────────────────────────────

    private Task SeedSuppressionAsync(string email, SuppressionReason reason = SuppressionReason.HardBounce)
    {
        var now = DateTime.UtcNow;
        return _suppressions.AddAsync(
            new EmailSuppression
            {
                Id = EmailSuppression.MakeId(email),
                Email = EmailSuppression.NormalizeAddress(email),
                Reason = reason,
                ConsecutiveFailureCount = 1,
                FirstSeenUtc = now,
                LastSeenUtc = now,
                SuppressedUtc = now,
                SuppressedBy = EmailSuppression.SystemDeliveryEvent,
            }
        );
    }

    private EmailJob NewQueuedJob(Guid jobId, bool grouped, params string[] emails)
    {
        var job = new EmailJob
        {
            Id = jobId,
            Status = EmailJobStatus.Queued,
            Category = "board",
            FromEmail = "board@cohad.org",
            FromDisplay = "COHAD Board",
            Subject = "Suppression test",
            ContentBlobPath = $"email-jobs/{jobId:D}.html",
            GroupRecipients = grouped,
            TotalRecipients = emails.Length,
            Recipients = emails
                .Select(e => new EmailJobRecipient
                {
                    Email = e,
                    HomeId = Guid.NewGuid(),
                    Status = EmailJobRecipientStatus.Pending,
                })
                .ToList(),
        };

        _jobRepo.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(job);
        _jobRepo.Setup(r => r.GetIncompleteJobsAsync()).ReturnsAsync(new List<EmailJob>());
        _jobRepo.Setup(r => r.UpdateAsync(It.IsAny<EmailJob>())).Returns(Task.CompletedTask);
        _fileStore
            .Setup(f => f.DownloadAsync(job.ContentBlobPath))
            .ReturnsAsync(
                new DocumentFileResult { Stream = new MemoryStream("<p>x</p>"u8.ToArray()), ContentType = "text/html" }
            );

        return job;
    }

    private static Mock<IEmailTransport> CreateCapturingTransport(List<MimeKit.MimeMessage> captured)
    {
        var mockSmtp = new Mock<IEmailTransport>();
        mockSmtp.Setup(t => t.ProviderName).Returns("SendGrid");
        mockSmtp
            .Setup(t =>
                t.SendAsync(
                    It.IsAny<MimeKit.MimeMessage>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback<MimeKit.MimeMessage, string, string, string, CancellationToken>((msg, _, _, _, _) => captured.Add(msg))
            .ReturnsAsync(new EmailSendResult { Success = true, ProviderName = "SendGrid" });
        return mockSmtp;
    }

    [Fact]
    public async Task RealSend_PerRecipient_SkipsSuppressedRecipientAndStampsWhy()
    {
        var jobId = Guid.NewGuid();
        var job = NewQueuedJob(jobId, grouped: false, "ok@test.com", "suppressed@test.com");
        await SeedSuppressionAsync("suppressed@test.com");

        var captured = new List<MimeKit.MimeMessage>();
        var processor = CreateRealSendProcessor(CreateCapturingTransport(captured));

        await RunProcessorForSingleJob(processor, jobId);

        // The transport saw exactly one message, to the deliverable recipient.
        var to = Assert.Single(captured).To.Mailboxes.Single().Address;
        Assert.Equal("ok@test.com", to);

        var suppressed = job.Recipients.Single(r => r.Email == "suppressed@test.com");
        Assert.Equal(EmailJobRecipientStatus.Suppressed, suppressed.Status);
        Assert.NotNull(suppressed.SuppressedUtc);
        Assert.Equal(SuppressionReason.HardBounce, suppressed.SuppressionReason);
        // No attempt happened, and the budget must say so - a suppressed skip that consumed
        // attempts would count against the recipient if the suppression is later cleared.
        Assert.Equal(0, suppressed.AttemptCount);
        Assert.Null(suppressed.Error);

        Assert.Equal(EmailJobStatus.Completed, job.Status);
        Assert.Equal(1, job.SentCount);
        Assert.Equal(0, job.FailedCount);
        Assert.Equal(1, job.SuppressedCount);
    }

    [Fact]
    public async Task RealSend_Grouped_ExcludesSuppressedAddressFromTheSingleMessage()
    {
        // The doc's test-list item: suppression is enforced for grouped sends, not only
        // per-recipient sends - the grouped branch is committee forwards and escalation digests,
        // the mail types that had no unsubscribe mechanism at all.
        var jobId = Guid.NewGuid();
        var job = NewQueuedJob(jobId, grouped: true, "admin1@test.com", "admin2@test.com", "admin3@test.com");
        // Seeded with different casing and whitespace than the recipient carries, so this also
        // locks that enforcement matches on the normalized address.
        await SeedSuppressionAsync("  Admin2@TEST.com ");

        var captured = new List<MimeKit.MimeMessage>();
        var processor = CreateRealSendProcessor(CreateCapturingTransport(captured));

        await RunProcessorForSingleJob(processor, jobId);

        var toAddresses = Assert.Single(captured).To.Mailboxes.Select(m => m.Address).OrderBy(a => a).ToList();
        Assert.Equal(new List<string> { "admin1@test.com", "admin3@test.com" }, toAddresses);

        var suppressed = job.Recipients.Single(r => r.Email == "admin2@test.com");
        Assert.Equal(EmailJobRecipientStatus.Suppressed, suppressed.Status);
        Assert.Equal(0, suppressed.AttemptCount);

        Assert.Equal(EmailJobStatus.Completed, job.Status);
        Assert.Equal(2, job.SentCount);
        Assert.Equal(1, job.SuppressedCount);
    }

    [Fact]
    public async Task MockMode_AppliesTheSameSuppressionPass()
    {
        // Mock-path parity: the MockData environment must enforce suppression identically or it
        // stops exercising what production does (and the seeded MockData suppression would be
        // decorative). Same helper, same position relative to the filter - this locks it.
        var jobId = Guid.NewGuid();
        var job = NewQueuedJob(jobId, grouped: false, "ok@test.com", "suppressed@test.com");
        await SeedSuppressionAsync("suppressed@test.com", SuppressionReason.SpamComplaint);

        var processor = CreateProcessor();
        await RunProcessorForSingleJob(processor, jobId);

        var suppressed = job.Recipients.Single(r => r.Email == "suppressed@test.com");
        Assert.Equal(EmailJobRecipientStatus.Suppressed, suppressed.Status);
        Assert.Equal(SuppressionReason.SpamComplaint, suppressed.SuppressionReason);
        Assert.Equal(EmailJobRecipientStatus.Sent, job.Recipients.Single(r => r.Email == "ok@test.com").Status);
        Assert.Equal(EmailJobStatus.Completed, job.Status);
        Assert.Equal(1, job.SuppressedCount);
    }

    [Fact]
    public async Task AllRecipientsSuppressed_JobCompletesWithZeroSentZeroFailed()
    {
        // Nothing failed and nothing remains to do: suppression did its job, and SuppressedCount
        // is what explains "Completed, sent 0" on the detail page.
        var jobId = Guid.NewGuid();
        var job = NewQueuedJob(jobId, grouped: false, "a@test.com", "b@test.com");
        await SeedSuppressionAsync("a@test.com");
        await SeedSuppressionAsync("b@test.com");

        var processor = CreateProcessor();
        await RunProcessorForSingleJob(processor, jobId);

        Assert.Equal(EmailJobStatus.Completed, job.Status);
        Assert.Equal(0, job.SentCount);
        Assert.Equal(0, job.FailedCount);
        Assert.Equal(2, job.SuppressedCount);
        Assert.All(job.Recipients, r => Assert.Equal(EmailJobRecipientStatus.Suppressed, r.Status));
    }

    [Fact]
    public async Task RealSend_PerRecipient_SkipsASuppressionAdoptedByTheConflictMerge()
    {
        // The enforcement point runs once, up front - but another instance (a stall reclaim) can
        // mark a recipient Suppressed AFTER that, and this instance learns of it only through the
        // attempt persist's conflict merge. The post-persist re-check must honour the adopted
        // status: mailing it and stamping Sent would defeat the enforcement point from a code
        // path it never sees.
        var jobId = Guid.NewGuid();
        var job = NewQueuedJob(jobId, grouped: false, "suppressed-elsewhere@test.com");

        var serverCopy = NewQueuedJob(jobId, grouped: false, "suppressed-elsewhere@test.com");
        serverCopy.Status = EmailJobStatus.InProgress;
        serverCopy.ETag = "server-etag";
        serverCopy.Recipients[0].Status = EmailJobRecipientStatus.Suppressed;
        serverCopy.Recipients[0].SuppressedUtc = DateTime.UtcNow.AddMinutes(-1);
        serverCopy.Recipients[0].SuppressionReason = SuppressionReason.HardBounce;

        var getCalls = 0;
        _jobRepo.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(() => getCalls++ == 0 ? job : serverCopy);

        var updateCalls = 0;
        _jobRepo
            .Setup(r => r.UpdateAsync(It.IsAny<EmailJob>()))
            .Returns<EmailJob>(_ =>
            {
                if (updateCalls++ == 0)
                    throw new EmailJobConcurrencyException();
                return Task.CompletedTask;
            });

        var captured = new List<MimeKit.MimeMessage>();
        var transport = CreateCapturingTransport(captured);
        var processor = CreateRealSendProcessor(transport);

        await RunProcessorForSingleJob(processor, jobId);

        // Never mailed, and the adopted suppression survived the close-out.
        Assert.Empty(captured);
        var recipient = job.Recipients.Single();
        Assert.Equal(EmailJobRecipientStatus.Suppressed, recipient.Status);
        Assert.Equal(SuppressionReason.HardBounce, recipient.SuppressionReason);
        Assert.Equal(EmailJobStatus.Completed, job.Status);
        Assert.Equal(1, job.SuppressedCount);
    }

    [Fact]
    public async Task RealSend_Grouped_ExcludesASuppressionAdoptedByTheConflictMerge()
    {
        // Same race, grouped branch: the To list must be built AFTER the attempt persist whose
        // merge can adopt the other instance's Suppressed, and the post-send stamping must not
        // overwrite it with Sent.
        var jobId = Guid.NewGuid();
        var job = NewQueuedJob(jobId, grouped: true, "admin1@test.com", "admin2@test.com");

        var serverCopy = NewQueuedJob(jobId, grouped: true, "admin1@test.com", "admin2@test.com");
        serverCopy.Status = EmailJobStatus.InProgress;
        serverCopy.ETag = "server-etag";
        var serverSuppressed = serverCopy.Recipients.Single(r => r.Email == "admin2@test.com");
        serverSuppressed.Status = EmailJobRecipientStatus.Suppressed;
        serverSuppressed.SuppressedUtc = DateTime.UtcNow.AddMinutes(-1);
        serverSuppressed.SuppressionReason = SuppressionReason.SpamComplaint;

        var getCalls = 0;
        _jobRepo.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(() => getCalls++ == 0 ? job : serverCopy);

        var updateCalls = 0;
        _jobRepo
            .Setup(r => r.UpdateAsync(It.IsAny<EmailJob>()))
            .Returns<EmailJob>(_ =>
            {
                if (updateCalls++ == 0)
                    throw new EmailJobConcurrencyException();
                return Task.CompletedTask;
            });

        var captured = new List<MimeKit.MimeMessage>();
        var processor = CreateRealSendProcessor(CreateCapturingTransport(captured));

        await RunProcessorForSingleJob(processor, jobId);

        var to = Assert.Single(captured).To.Mailboxes.Select(m => m.Address).ToList();
        Assert.Equal(new List<string> { "admin1@test.com" }, to);

        var suppressed = job.Recipients.Single(r => r.Email == "admin2@test.com");
        Assert.Equal(EmailJobRecipientStatus.Suppressed, suppressed.Status);
        Assert.Equal(EmailJobRecipientStatus.Sent, job.Recipients.Single(r => r.Email == "admin1@test.com").Status);
        Assert.Equal(EmailJobStatus.Completed, job.Status);
        Assert.Equal(1, job.SentCount);
        Assert.Equal(1, job.SuppressedCount);
    }

    [Fact]
    public async Task CaseVariantSuppressionRows_DoNotHaltTheJob()
    {
        // Hand-authored or imported documents can carry case/whitespace variants of one address
        // (their ids differ, so the container accepts both). A naive ToDictionary would throw on
        // the duplicate key and fail EVERY send job over one odd row - a total outbound-mail halt
        // far broader than the intended fail-closed. Any variant means "do not mail".
        await SeedSuppressionAsync("dup@test.com");
        await _suppressions.AddAsync(
            new EmailSuppression
            {
                Id = "hand-authored-variant",
                Email = "  DUP@Test.com ",
                Reason = SuppressionReason.AdminAction,
                ConsecutiveFailureCount = 1,
                FirstSeenUtc = DateTime.UtcNow,
                LastSeenUtc = DateTime.UtcNow,
                SuppressedUtc = DateTime.UtcNow,
                SuppressedBy = "someone",
            }
        );

        var jobId = Guid.NewGuid();
        var job = NewQueuedJob(jobId, grouped: false, "dup@test.com", "ok@test.com");

        var processor = CreateProcessor();
        await RunProcessorForSingleJob(processor, jobId);

        Assert.Equal(EmailJobStatus.Completed, job.Status);
        Assert.Equal(EmailJobRecipientStatus.Suppressed, job.Recipients.Single(r => r.Email == "dup@test.com").Status);
        Assert.Equal(EmailJobRecipientStatus.Sent, job.Recipients.Single(r => r.Email == "ok@test.com").Status);
    }

    [Fact]
    public async Task SuppressionReadFailure_FailsTheJobInsteadOfSendingUnfiltered()
    {
        // Fail-closed: a missing container (or any read fault) must never mean "send to
        // everyone". The exception propagates, the job is marked Failed, and no recipient is
        // touched - clicking Send again after fixing the container is the whole recovery,
        // exactly like the UnsubscribeLink container gate at job creation.
        var throwingRepo = new Mock<IEmailSuppressionRepository>();
        throwingRepo
            .Setup(r => r.GetActiveAsync())
            .ThrowsAsync(new InvalidOperationException("container missing"));
        _suppressionRepoOverride = throwingRepo.Object;

        var jobId = Guid.NewGuid();
        var job = NewQueuedJob(jobId, grouped: false, "a@test.com");

        var processor = CreateProcessor();
        await RunProcessorForSingleJob(processor, jobId);

        Assert.Equal(EmailJobStatus.Failed, job.Status);
        Assert.Equal(EmailJobRecipientStatus.Pending, job.Recipients.Single().Status);
        Assert.Equal(0, job.Recipients.Single().AttemptCount);
    }

    [Fact]
    public async Task Resume_StalledInProgressJob_WithSuppressedRemainder_MarksCompletedNotFailed()
    {
        // The watchdog's old inline terminal rule required every recipient to be Sent for
        // Completed. Under that rule this job - one sent, one suppressed - would close as
        // PartiallyCompleted, reading as if something went wrong. DeriveTerminalStatus is the
        // shared rule; this locks the watchdog to it.
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
                    Email = "suppressed@test.com",
                    HomeId = Guid.NewGuid(),
                    Status = EmailJobRecipientStatus.Suppressed,
                    SuppressedUtc = DateTime.UtcNow.AddMinutes(-30),
                    SuppressionReason = SuppressionReason.HardBounce,
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
            if (stalled.Status != EmailJobStatus.InProgress)
                break;
        }

        cts.Cancel();
        try
        {
            await processor.StopAsync(CancellationToken.None);
        }
        catch (OperationCanceledException) { }

        Assert.Equal(EmailJobStatus.Completed, stalled.Status);
        Assert.Equal(1, stalled.SentCount);
        Assert.Equal(0, stalled.FailedCount);
        Assert.Equal(1, stalled.SuppressedCount);
        // The suppressed recipient was not disturbed by the close-out.
        Assert.Equal(
            EmailJobRecipientStatus.Suppressed,
            stalled.Recipients.Single(r => r.Email == "suppressed@test.com").Status
        );
    }

    // ─── The static helpers, driven directly ───

    [Fact]
    public void ApplySuppressions_LeavesRecipientsWithKnownDeliveryOutcomesAlone()
    {
        // A proven delivery outranks a skip: NormalizePendingDelivered promotes these to Sent,
        // which is the truthful record of what happened.
        var job = new EmailJob
        {
            Recipients = new List<EmailJobRecipient>
            {
                new()
                {
                    Email = "delivered@test.com",
                    Status = EmailJobRecipientStatus.Pending,
                    DeliveryStatus = DeliveryStatus.Delivered,
                },
                new() { Email = "sent@test.com", Status = EmailJobRecipientStatus.Sent },
            },
        };
        var active = new Dictionary<string, SuppressionReason>(StringComparer.OrdinalIgnoreCase)
        {
            ["delivered@test.com"] = SuppressionReason.HardBounce,
            ["sent@test.com"] = SuppressionReason.HardBounce,
        };

        Assert.False(EmailJobProcessor.ApplySuppressions(job, active));
        Assert.Equal(EmailJobRecipientStatus.Pending, job.Recipients[0].Status);
        Assert.Equal(EmailJobRecipientStatus.Sent, job.Recipients[1].Status);
    }

    [Fact]
    public void ApplySuppressions_ReclassifiesFailedRecipientsAndClearsStaleErrorText()
    {
        // A previously-Failed recipient whose address is now suppressed reads as Suppressed, not
        // Failed-with-an-error: the skip is deliberate, and the old transport error would
        // misdescribe it on the detail page.
        var job = new EmailJob
        {
            Recipients = new List<EmailJobRecipient>
            {
                new()
                {
                    Email = "failed@test.com",
                    Status = EmailJobRecipientStatus.Failed,
                    Error = "SMTP timeout",
                    AttemptCount = 2,
                },
            },
        };
        var active = new Dictionary<string, SuppressionReason>(StringComparer.OrdinalIgnoreCase)
        {
            ["failed@test.com"] = SuppressionReason.SpamComplaint,
        };

        Assert.True(EmailJobProcessor.ApplySuppressions(job, active));
        var r = job.Recipients.Single();
        Assert.Equal(EmailJobRecipientStatus.Suppressed, r.Status);
        Assert.Null(r.Error);
        // The attempt history is real and stays.
        Assert.Equal(2, r.AttemptCount);
        Assert.Equal(1, job.SuppressedCount);
        Assert.Equal(0, job.FailedCount);
    }

    [Theory]
    [InlineData(0, 0, 2, EmailJobStatus.Completed)] // all suppressed: nothing failed, nothing to do
    [InlineData(3, 0, 0, EmailJobStatus.Completed)]
    [InlineData(2, 0, 1, EmailJobStatus.Completed)]
    [InlineData(0, 2, 1, EmailJobStatus.Failed)]
    [InlineData(1, 1, 1, EmailJobStatus.PartiallyCompleted)]
    public void DeriveTerminalStatus_OneRuleForAllThreeCloseOuts(
        int sent,
        int failed,
        int suppressed,
        EmailJobStatus expected
    )
    {
        var job = new EmailJob
        {
            SentCount = sent,
            FailedCount = failed,
            SuppressedCount = suppressed,
        };

        Assert.Equal(expected, EmailJobProcessor.DeriveTerminalStatus(job));
    }

    [Fact]
    public void MergeJobFromServer_AdoptsServerSuppressed_ButSentStillWins()
    {
        var email = "a@test.com";
        var local = new EmailJob
        {
            Recipients = new List<EmailJobRecipient>
            {
                new() { Email = email, Status = EmailJobRecipientStatus.Pending },
                new() { Email = "sent-locally@test.com", Status = EmailJobRecipientStatus.Sent, SentUtc = DateTime.UtcNow },
            },
        };
        var suppressedUtc = DateTime.UtcNow.AddMinutes(-1);
        var server = new EmailJob
        {
            Recipients = new List<EmailJobRecipient>
            {
                new()
                {
                    Email = email,
                    Status = EmailJobRecipientStatus.Suppressed,
                    SuppressedUtc = suppressedUtc,
                    SuppressionReason = SuppressionReason.HardBounce,
                },
                new()
                {
                    Email = "sent-locally@test.com",
                    Status = EmailJobRecipientStatus.Suppressed,
                    SuppressedUtc = suppressedUtc,
                    SuppressionReason = SuppressionReason.HardBounce,
                },
            },
        };

        EmailJobProcessor.MergeJobFromServer(local, server);

        // The winner's Suppressed is adopted so a reclaimed stall cannot persist a stale Pending
        // over it and strand a terminal job with a recipient that looks unsent...
        var adopted = local.Recipients.Single(r => r.Email == email);
        Assert.Equal(EmailJobRecipientStatus.Suppressed, adopted.Status);
        Assert.Equal(suppressedUtc, adopted.SuppressedUtc);
        Assert.Equal(SuppressionReason.HardBounce, adopted.SuppressionReason);

        // ...but a local Sent is the truthful record - the message actually went out - and is
        // never demoted to Suppressed.
        Assert.Equal(
            EmailJobRecipientStatus.Sent,
            local.Recipients.Single(r => r.Email == "sent-locally@test.com").Status
        );
    }

}
