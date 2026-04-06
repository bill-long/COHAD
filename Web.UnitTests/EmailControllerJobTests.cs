using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Web.Controllers;
using Web.Hubs;
using Web.Models;
using Web.PresentationModels;
using Web.Services;
using Web.Services.Repositories;
using Web.UpdateModels;

namespace Web.UnitTests;

public sealed class EmailControllerJobTests
{
    private const string IdentityProviderClaim = "http://schemas.microsoft.com/identity/claims/identityprovider";

    private readonly Mock<IUserRepository> _users = new();
    private readonly Mock<IHomeRepository> _homes = new();
    private readonly Mock<IResidentRepository> _residents = new();
    private readonly Mock<IAuditLogRepository> _audit = new();
    private readonly Mock<IEmailJobRepository> _jobRepo = new();
    private readonly Mock<IDocumentFileStore> _fileStore = new();
    private readonly EmailJobCleanupService _cleanup;
    private readonly EmailJobQueue _queue = new();

    public EmailControllerJobTests()
    {
        // Default resident mock returns empty so tests don't crash.
        _residents.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Resident>());

        // Default cleanup query returns empty so tests exercise the non-exception path.
        _jobRepo
            .Setup(r => r.GetTerminalJobsOlderThanAsync(It.IsAny<DateTime>(), It.IsAny<int>()))
            .ReturnsAsync(new List<EmailJob>());

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["EmailJobs:RetentionDays"] = "30",
                    ["EmailJobs:CleanupBatchSize"] = "25",
                }
            )
            .Build();

        // Use a real cleanup service wired to our mocks. The controller treats cleanup as best-effort.
        _cleanup = new EmailJobCleanupService(
            _jobRepo.Object,
            _fileStore.Object,
            config,
            NullLogger<EmailJobCleanupService>.Instance
        );
    }

    private EmailJobProcessor CreateProcessor()
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
        var mockClientProxy = new Mock<IClientProxy>();
        mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(mockClientProxy.Object);
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
                    ["EmailJobs:Mock:DelayMilliseconds"] = "0",
                }
            )
            .Build();

        var tokenService = new Mock<IUnsubscribeTokenService>();

        return new EmailJobProcessor(
            _queue,
            scopeFactory.Object,
            tokenService.Object,
            hubContext.Object,
            config,
            env.Object,
            NullLogger<EmailJobProcessor>.Instance
        );
    }

    private EmailController CreateController(EmailJobProcessor? processor = null)
    {
        processor ??= CreateProcessor();

        var controller = new EmailController(
            _users.Object,
            _homes.Object,
            _residents.Object,
            _audit.Object,
            _jobRepo.Object,
            _fileStore.Object,
            _queue,
            processor,
            _cleanup,
            NullLogger<EmailController>.Instance
        )
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(
                        new ClaimsIdentity(
                            new[]
                            {
                                new Claim(ClaimTypes.NameIdentifier, "u1"),
                                new Claim(IdentityProviderClaim, "google.com"),
                                new Claim("emails", "test@example.com"),
                            },
                            "Test"
                        )
                    ),
                },
            },
        };
        return controller;
    }

    private static readonly Guid TestHomeId = Guid.NewGuid();

    private void SetupMockUser()
    {
        _users
            .Setup(r => r.GetByUniqueIdAsync(It.IsAny<string>()))
            .ReturnsAsync(
                new User
                {
                    UniqueId = "google.comu1",
                    GivenName = "Test",
                    Surname = "User",
                    Emails = "test@example.com",
                    Roles = new List<User.Role> { User.Role.Board },
                    OwnedHomeIds = new List<Guid> { TestHomeId },
                }
            );
    }

    private void SetupMockUserHomes(params string[] emails)
    {
        var home = new Home
        {
            Id = TestHomeId,
            EmailAddress = new EmailAddress { Address = emails.Length > 0 ? emails[0] : "test@example.com" },
            Residents = new List<Resident>(),
        };

        _homes
            .Setup(r => r.GetByIdsAsync(It.Is<List<Guid>>(ids => ids.Contains(TestHomeId))))
            .ReturnsAsync(new List<Home> { home });

        var residents = emails
            .Select(e => new Resident
            {
                HomeId = TestHomeId,
                EmailAddresses = new List<EmailAddress> { new EmailAddress { Address = e } },
            })
            .ToList();

        _residents.Setup(r => r.GetByHomeIdsAsync(It.IsAny<IEnumerable<Guid>>())).ReturnsAsync(residents);
    }

    private void SetupHomesWithRecipients(int count = 2)
    {
        var homes = Enumerable
            .Range(1, count)
            .Select(i => new Home
            {
                Id = Guid.NewGuid(),
                EmailAddress = new EmailAddress
                {
                    Address = $"home{i}@test.com",
                    BoardEmailOptedIn = true,
                    WelcomeEmailOptedIn = true,
                    GardenClubEmailOptedIn = true,
                    SocialCommitteeEmailOptedIn = true,
                    SunshineCommitteeEmailOptedIn = true,
                },
                Residents = new List<Resident>(),
            })
            .ToList();

        _homes.Setup(r => r.GetAllAsync()).ReturnsAsync(homes);
    }

    // ───────────────────────────────────────────────────
    // SendCommitteeEmail tests (via SendEmailFromBoard)
    // ───────────────────────────────────────────────────

    [Fact]
    public async Task SendEmailFromBoard_TestEmail_CreatesJobAndReturns202()
    {
        SetupMockUser();
        SetupMockUserHomes("test@example.com", "other@example.com");

        _fileStore
            .Setup(f => f.UploadAsync(It.IsAny<string>(), It.IsAny<Stream>(), "text/html"))
            .Returns(Task.CompletedTask);
        _jobRepo.Setup(r => r.AddAsync(It.IsAny<EmailJob>())).Returns(Task.CompletedTask);

        var emailInfo = new EmailInfo
        {
            Subject = "Test",
            HtmlBody = "<p>hi</p>",
            IsTestEmail = true,
            TestRecipientEmails = new List<string> { "test@example.com" },
        };

        var controller = CreateController();
        var result = await controller.SendEmailFromBoard(emailInfo);

        var accepted = Assert.IsType<AcceptedResult>(result);
        var summary = Assert.IsType<EmailJobSummary>(accepted.Value);
        Assert.Equal("Test: Test", summary.Subject);
        Assert.Equal(1, summary.TotalRecipients);
        Assert.Equal(EmailJobStatus.Queued, summary.Status);

        _jobRepo.Verify(
            r =>
                r.AddAsync(
                    It.Is<EmailJob>(j =>
                        j.Subject == "Test: Test"
                        && j.TotalRecipients == 1
                        && j.Recipients[0].Email == "test@example.com"
                    )
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task SendEmailFromBoard_TestEmail_MultipleRecipients_CreatesJob()
    {
        SetupMockUser();
        SetupMockUserHomes("a@example.com", "b@example.com");

        _fileStore
            .Setup(f => f.UploadAsync(It.IsAny<string>(), It.IsAny<Stream>(), "text/html"))
            .Returns(Task.CompletedTask);
        _jobRepo.Setup(r => r.AddAsync(It.IsAny<EmailJob>())).Returns(Task.CompletedTask);

        var emailInfo = new EmailInfo
        {
            Subject = "Multi",
            HtmlBody = "<p>hi</p>",
            IsTestEmail = true,
            TestRecipientEmails = new List<string> { "a@example.com", "b@example.com" },
        };

        var controller = CreateController();
        var result = await controller.SendEmailFromBoard(emailInfo);

        var accepted = Assert.IsType<AcceptedResult>(result);
        var summary = Assert.IsType<EmailJobSummary>(accepted.Value);
        Assert.Equal(2, summary.TotalRecipients);
    }

    [Fact]
    public async Task SendEmailFromBoard_TestEmail_NoRecipients_Returns400()
    {
        SetupMockUser();

        var emailInfo = new EmailInfo
        {
            Subject = "Test",
            HtmlBody = "<p>hi</p>",
            IsTestEmail = true,
            TestRecipientEmails = new List<string>(),
        };

        var controller = CreateController();
        var result = await controller.SendEmailFromBoard(emailInfo);

        Assert.IsType<BadRequestObjectResult>(result);
        _jobRepo.Verify(r => r.AddAsync(It.IsAny<EmailJob>()), Times.Never);
    }

    [Fact]
    public async Task SendEmailFromBoard_TestEmail_NullRecipients_Returns400()
    {
        SetupMockUser();

        var emailInfo = new EmailInfo
        {
            Subject = "Test",
            HtmlBody = "<p>hi</p>",
            IsTestEmail = true,
            TestRecipientEmails = null,
        };

        var controller = CreateController();
        var result = await controller.SendEmailFromBoard(emailInfo);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task SendEmailFromBoard_TestEmail_InvalidEmail_Returns400()
    {
        SetupMockUser();
        SetupMockUserHomes("test@example.com");

        var emailInfo = new EmailInfo
        {
            Subject = "Test",
            HtmlBody = "<p>hi</p>",
            IsTestEmail = true,
            TestRecipientEmails = new List<string> { "hacker@evil.com" },
        };

        var controller = CreateController();
        var result = await controller.SendEmailFromBoard(emailInfo);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("hacker@evil.com", bad.Value?.ToString());
        _jobRepo.Verify(r => r.AddAsync(It.IsAny<EmailJob>()), Times.Never);
    }

    [Fact]
    public async Task SendEmailFromBoard_NonTest_CreatesJobAndReturns202()
    {
        SetupMockUser();
        SetupHomesWithRecipients(3);

        _fileStore
            .Setup(f => f.UploadAsync(It.IsAny<string>(), It.IsAny<Stream>(), "text/html"))
            .Returns(Task.CompletedTask);
        _jobRepo.Setup(r => r.AddAsync(It.IsAny<EmailJob>())).Returns(Task.CompletedTask);

        var emailInfo = new EmailInfo
        {
            Subject = "Newsletter",
            HtmlBody = "<p>content</p>",
            IsTestEmail = false,
        };

        var controller = CreateController();
        var result = await controller.SendEmailFromBoard(emailInfo);

        var accepted = Assert.IsType<AcceptedResult>(result);
        var summary = Assert.IsType<EmailJobSummary>(accepted.Value);
        Assert.Equal("board", summary.Category);
        Assert.Equal("Newsletter", summary.Subject);
        Assert.Equal(3, summary.TotalRecipients);
        Assert.Equal(EmailJobStatus.Queued, summary.Status);

        _jobRepo.Verify(
            r =>
                r.AddAsync(
                    It.Is<EmailJob>(j =>
                        j.Category == "board"
                        && j.Subject == "Newsletter"
                        && j.TotalRecipients == 3
                        && j.Status == EmailJobStatus.Queued
                        && j.ContentBlobPath.StartsWith("email-jobs/")
                    )
                ),
            Times.Once
        );

        _fileStore.Verify(
            f => f.UploadAsync(It.Is<string>(p => p.StartsWith("email-jobs/")), It.IsAny<Stream>(), "text/html"),
            Times.Once
        );

        _audit.Verify(a => a.AddAsync(It.IsAny<NewAuditLogEntry>()), Times.Once);
    }

    [Fact]
    public async Task SendEmailFromBoard_NoRecipients_ReturnsOkMessage()
    {
        SetupMockUser();
        _homes.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Home>());

        var emailInfo = new EmailInfo
        {
            Subject = "Test",
            HtmlBody = "<p>hi</p>",
            IsTestEmail = false,
        };

        var controller = CreateController();
        var result = await controller.SendEmailFromBoard(emailInfo);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
        _jobRepo.Verify(r => r.AddAsync(It.IsAny<EmailJob>()), Times.Never);
    }

    [Fact]
    public async Task SendEmailFromBoard_DeduplicatesRecipients()
    {
        SetupMockUser();

        // Two homes with the same email address — should only get one recipient
        var homes = new List<Home>
        {
            new Home
            {
                Id = Guid.NewGuid(),
                EmailAddress = new EmailAddress { Address = "shared@test.com", BoardEmailOptedIn = true },
                Residents = new List<Resident>(),
            },
            new Home
            {
                Id = Guid.NewGuid(),
                EmailAddress = new EmailAddress { Address = "shared@test.com", BoardEmailOptedIn = true },
                Residents = new List<Resident>(),
            },
        };
        _homes.Setup(r => r.GetAllAsync()).ReturnsAsync(homes);
        _fileStore
            .Setup(f => f.UploadAsync(It.IsAny<string>(), It.IsAny<Stream>(), "text/html"))
            .Returns(Task.CompletedTask);
        _jobRepo.Setup(r => r.AddAsync(It.IsAny<EmailJob>())).Returns(Task.CompletedTask);

        var emailInfo = new EmailInfo
        {
            Subject = "Dup test",
            HtmlBody = "<p>hi</p>",
            IsTestEmail = false,
        };
        var controller = CreateController();
        var result = await controller.SendEmailFromBoard(emailInfo);

        var accepted = Assert.IsType<AcceptedResult>(result);
        var summary = Assert.IsType<EmailJobSummary>(accepted.Value);
        Assert.Equal(1, summary.TotalRecipients);
    }

    [Fact]
    public async Task SendEmailFromBoard_FiltersOptedOutRecipients()
    {
        SetupMockUser();

        var homes = new List<Home>
        {
            new Home
            {
                Id = Guid.NewGuid(),
                EmailAddress = new EmailAddress { Address = "optedIn@test.com", BoardEmailOptedIn = true },
                Residents = new List<Resident>(),
            },
            new Home
            {
                Id = Guid.NewGuid(),
                EmailAddress = new EmailAddress { Address = "optedOut@test.com", BoardEmailOptedIn = false },
                Residents = new List<Resident>(),
            },
        };
        _homes.Setup(r => r.GetAllAsync()).ReturnsAsync(homes);
        _fileStore
            .Setup(f => f.UploadAsync(It.IsAny<string>(), It.IsAny<Stream>(), "text/html"))
            .Returns(Task.CompletedTask);
        _jobRepo.Setup(r => r.AddAsync(It.IsAny<EmailJob>())).Returns(Task.CompletedTask);

        var emailInfo = new EmailInfo
        {
            Subject = "Filter",
            HtmlBody = "<p>hi</p>",
            IsTestEmail = false,
        };
        var controller = CreateController();
        var result = await controller.SendEmailFromBoard(emailInfo);

        var accepted = Assert.IsType<AcceptedResult>(result);
        var summary = Assert.IsType<EmailJobSummary>(accepted.Value);
        Assert.Equal(1, summary.TotalRecipients);
    }

    // ───────────────────────────────────────────────────
    // Job management endpoint tests
    // ───────────────────────────────────────────────────

    [Fact]
    public async Task GetRecentJobs_ReturnsJobSummaries()
    {
        var jobs = new List<EmailJob>
        {
            new EmailJob
            {
                Id = Guid.NewGuid(),
                Status = EmailJobStatus.Completed,
                Category = "board",
                Subject = "Job 1",
                CreatedUtc = DateTime.UtcNow.AddHours(-2),
                TotalRecipients = 10,
                SentCount = 10,
                Recipients = new List<EmailJobRecipient>(),
            },
            new EmailJob
            {
                Id = Guid.NewGuid(),
                Status = EmailJobStatus.InProgress,
                Category = "welcome",
                Subject = "Job 2",
                CreatedUtc = DateTime.UtcNow.AddHours(-1),
                TotalRecipients = 5,
                SentCount = 3,
                Recipients = new List<EmailJobRecipient>(),
            },
        };
        _jobRepo.Setup(r => r.GetRecentJobsAsync(50)).ReturnsAsync(jobs);

        var controller = CreateController();
        var result = await controller.GetRecentJobs();

        var ok = Assert.IsType<OkObjectResult>(result);
        var summaries = Assert.IsAssignableFrom<IEnumerable<EmailJobSummary>>(ok.Value);
        Assert.Equal(2, summaries.Count());
    }

    [Fact]
    public async Task GetJob_ExistingJob_ReturnsDetail()
    {
        var jobId = Guid.NewGuid();
        var job = new EmailJob
        {
            Id = jobId,
            Status = EmailJobStatus.Completed,
            Category = "board",
            Subject = "Test Job",
            CreatedUtc = DateTime.UtcNow,
            TotalRecipients = 2,
            SentCount = 2,
            Recipients = new List<EmailJobRecipient>
            {
                new EmailJobRecipient { Email = "a@test.com", Status = EmailJobRecipientStatus.Sent },
                new EmailJobRecipient { Email = "b@test.com", Status = EmailJobRecipientStatus.Sent },
            },
        };
        _jobRepo.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(job);

        var controller = CreateController();
        var result = await controller.GetJob(jobId);

        var ok = Assert.IsType<OkObjectResult>(result);
        var detail = Assert.IsType<EmailJobDetail>(ok.Value);
        Assert.Equal(jobId, detail.Id);
        Assert.Equal(2, detail.Recipients.Count);
        Assert.All(detail.Recipients, r => Assert.Equal(EmailJobRecipientStatus.Sent, r.Status));
    }

    [Fact]
    public async Task GetJob_NotFound_Returns404()
    {
        var jobId = Guid.NewGuid();
        _jobRepo.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync((EmailJob?)null);

        var controller = CreateController();
        var result = await controller.GetJob(jobId);

        Assert.IsType<NotFoundResult>(result);
    }

    // ───────────────────────────────────────────────────
    // Retry tests
    // ───────────────────────────────────────────────────

    [Fact]
    public async Task RetryJob_FailedJob_ResetsAndEnqueues()
    {
        var jobId = Guid.NewGuid();
        var job = new EmailJob
        {
            Id = jobId,
            Status = EmailJobStatus.Failed,
            FailedCount = 2,
            SentCount = 1,
            LastError = "SMTP error",
            CompletedUtc = DateTime.UtcNow.AddMinutes(-5),
            TotalRecipients = 3,
            Recipients = new List<EmailJobRecipient>
            {
                new EmailJobRecipient { Email = "a@test.com", Status = EmailJobRecipientStatus.Sent },
                new EmailJobRecipient
                {
                    Email = "b@test.com",
                    Status = EmailJobRecipientStatus.Failed,
                    Error = "timeout",
                },
                new EmailJobRecipient
                {
                    Email = "c@test.com",
                    Status = EmailJobRecipientStatus.Failed,
                    Error = "refused",
                },
            },
        };
        _jobRepo.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(job);
        _jobRepo.Setup(r => r.UpdateAsync(It.IsAny<EmailJob>())).Returns(Task.CompletedTask);

        var controller = CreateController();
        var result = await controller.RetryJob(jobId);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.IsType<EmailJobSummary>(ok.Value);

        // Failed recipients should be reset to Pending
        Assert.Equal(EmailJobRecipientStatus.Pending, job.Recipients[1].Status);
        Assert.Null(job.Recipients[1].Error);
        Assert.Equal(EmailJobRecipientStatus.Pending, job.Recipients[2].Status);
        Assert.Null(job.Recipients[2].Error);

        // Sent recipient should be untouched
        Assert.Equal(EmailJobRecipientStatus.Sent, job.Recipients[0].Status);

        // Job-level fields should be reset
        Assert.Equal(EmailJobStatus.Queued, job.Status);
        Assert.Equal(0, job.FailedCount);
        Assert.Null(job.LastError);
        Assert.Null(job.CompletedUtc);

        _jobRepo.Verify(r => r.UpdateAsync(job), Times.Once);
    }

    [Theory]
    [InlineData(EmailJobStatus.PartiallyCompleted)]
    [InlineData(EmailJobStatus.Cancelled)]
    public async Task RetryJob_AllowedStatuses_Succeeds(EmailJobStatus status)
    {
        var jobId = Guid.NewGuid();
        var job = new EmailJob
        {
            Id = jobId,
            Status = status,
            Recipients = new List<EmailJobRecipient>
            {
                new EmailJobRecipient
                {
                    Email = "a@test.com",
                    Status = EmailJobRecipientStatus.Failed,
                    Error = "err",
                },
            },
        };
        _jobRepo.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(job);
        _jobRepo.Setup(r => r.UpdateAsync(It.IsAny<EmailJob>())).Returns(Task.CompletedTask);

        var controller = CreateController();
        var result = await controller.RetryJob(jobId);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(EmailJobStatus.Queued, job.Status);
    }

    [Theory]
    [InlineData(EmailJobStatus.Completed)]
    [InlineData(EmailJobStatus.Queued)]
    [InlineData(EmailJobStatus.InProgress)]
    public async Task RetryJob_DisallowedStatus_ReturnsBadRequest(EmailJobStatus status)
    {
        var jobId = Guid.NewGuid();
        var job = new EmailJob
        {
            Id = jobId,
            Status = status,
            Recipients = new List<EmailJobRecipient>(),
        };
        _jobRepo.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(job);

        var controller = CreateController();
        var result = await controller.RetryJob(jobId);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task RetryJob_NotFound_Returns404()
    {
        var jobId = Guid.NewGuid();
        _jobRepo.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync((EmailJob?)null);

        var controller = CreateController();
        var result = await controller.RetryJob(jobId);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task RetryJob_UpdateConcurrency_ReturnsConflict()
    {
        var jobId = Guid.NewGuid();
        var job = new EmailJob
        {
            Id = jobId,
            Status = EmailJobStatus.Failed,
            Recipients = new List<EmailJobRecipient>(),
            TotalRecipients = 0,
        };
        _jobRepo.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(job);
        _jobRepo.Setup(r => r.UpdateAsync(It.IsAny<EmailJob>())).ThrowsAsync(new EmailJobConcurrencyException());

        var controller = CreateController();
        var result = await controller.RetryJob(jobId);

        Assert.IsType<ConflictObjectResult>(result);
    }

    // ───────────────────────────────────────────────────
    // Cancel tests
    // ───────────────────────────────────────────────────

    [Fact]
    public async Task CancelJob_InProgressJob_SetsStatusCancelled()
    {
        var jobId = Guid.NewGuid();
        var job = new EmailJob
        {
            Id = jobId,
            Status = EmailJobStatus.InProgress,
            Recipients = new List<EmailJobRecipient>(),
        };
        _jobRepo.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(job);
        _jobRepo.Setup(r => r.UpdateAsync(It.IsAny<EmailJob>())).Returns(Task.CompletedTask);

        var controller = CreateController();
        var result = await controller.CancelJob(jobId);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.IsType<EmailJobSummary>(ok.Value);
        Assert.Equal(EmailJobStatus.Cancelled, job.Status);
        Assert.NotNull(job.CompletedUtc);
        _jobRepo.Verify(r => r.UpdateAsync(job), Times.Once);
    }

    [Fact]
    public async Task CancelJob_QueuedJob_SetsStatusCancelled()
    {
        var jobId = Guid.NewGuid();
        var job = new EmailJob
        {
            Id = jobId,
            Status = EmailJobStatus.Queued,
            Recipients = new List<EmailJobRecipient>(),
        };
        _jobRepo.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(job);
        _jobRepo.Setup(r => r.UpdateAsync(It.IsAny<EmailJob>())).Returns(Task.CompletedTask);

        var controller = CreateController();
        var result = await controller.CancelJob(jobId);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(EmailJobStatus.Cancelled, job.Status);
    }

    [Fact]
    public async Task CancelJob_UpdateConcurrency_RefetchesAndReturnsOk()
    {
        var jobId = Guid.NewGuid();
        var queued = new EmailJob
        {
            Id = jobId,
            Status = EmailJobStatus.Queued,
            CreatedUtc = DateTime.UtcNow,
            Recipients = new List<EmailJobRecipient>(),
        };
        var completedElsewhere = new EmailJob
        {
            Id = jobId,
            Status = EmailJobStatus.Completed,
            CreatedUtc = queued.CreatedUtc,
            CompletedUtc = DateTime.UtcNow,
            Recipients = new List<EmailJobRecipient>(),
        };
        _jobRepo
            .SetupSequence(r => r.GetByIdAsync(jobId))
            .ReturnsAsync(queued)
            .ReturnsAsync(queued)
            .ReturnsAsync(completedElsewhere);
        _jobRepo.Setup(r => r.UpdateAsync(It.IsAny<EmailJob>())).ThrowsAsync(new EmailJobConcurrencyException());

        var controller = CreateController();
        var result = await controller.CancelJob(jobId);

        var ok = Assert.IsType<OkObjectResult>(result);
        var summary = Assert.IsType<EmailJobSummary>(ok.Value);
        Assert.Equal(EmailJobStatus.Completed, summary.Status);
    }

    [Theory]
    [InlineData(EmailJobStatus.Completed)]
    [InlineData(EmailJobStatus.Cancelled)]
    public async Task CancelJob_DisallowedStatus_ReturnsBadRequest(EmailJobStatus status)
    {
        var jobId = Guid.NewGuid();
        var job = new EmailJob
        {
            Id = jobId,
            Status = status,
            Recipients = new List<EmailJobRecipient>(),
        };
        _jobRepo.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(job);

        var controller = CreateController();
        var result = await controller.CancelJob(jobId);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task CancelJob_NotFound_Returns404()
    {
        var jobId = Guid.NewGuid();
        _jobRepo.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync((EmailJob?)null);

        var controller = CreateController();
        var result = await controller.CancelJob(jobId);

        Assert.IsType<NotFoundResult>(result);
    }

    // ───────────────────────────────────────────────────
    // GetRecentJobs limit clamping
    // ───────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(200, 100)]
    [InlineData(50, 50)]
    public async Task GetRecentJobs_ClampsLimit(int input, int expected)
    {
        _jobRepo.Setup(r => r.GetRecentJobsAsync(expected)).ReturnsAsync(new List<EmailJob>());

        var controller = CreateController();
        await controller.GetRecentJobs(input);

        _jobRepo.Verify(r => r.GetRecentJobsAsync(expected), Times.Once);
    }

    // ───────────────────────────────────────────────────
    // GetTestRecipients tests
    // ───────────────────────────────────────────────────

    [Fact]
    public async Task GetTestRecipients_ReturnsEmailsFromUserHomes()
    {
        SetupMockUser();
        SetupMockUserHomes("a@example.com", "b@example.com");

        var controller = CreateController();
        var result = await controller.GetTestRecipients();

        var ok = Assert.IsType<OkObjectResult>(result);
        var items = Assert.IsAssignableFrom<List<TestRecipientOption>>(ok.Value);
        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.Email == "a@example.com" && i.HomeId == TestHomeId);
        Assert.Contains(items, i => i.Email == "b@example.com" && i.HomeId == TestHomeId);
    }

    [Fact]
    public async Task GetTestRecipients_NoHomes_ReturnsEmpty()
    {
        _users
            .Setup(r => r.GetByUniqueIdAsync(It.IsAny<string>()))
            .ReturnsAsync(
                new User
                {
                    UniqueId = "google.comu1",
                    GivenName = "Test",
                    Surname = "User",
                    Emails = "test@example.com",
                    Roles = new List<User.Role> { User.Role.Board },
                    OwnedHomeIds = new List<Guid>(),
                }
            );

        var controller = CreateController();
        var result = await controller.GetTestRecipients();

        var ok = Assert.IsType<OkObjectResult>(result);
        var items = Assert.IsAssignableFrom<List<TestRecipientOption>>(ok.Value);
        Assert.Empty(items);
    }
}
