using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Web.Models;
using Web.Services;
using Web.Services.Repositories;

namespace Web.UnitTests;

public sealed class EmailJobCleanupServiceTests
{
    [Fact]
    public async Task RunOnceBestEffortAsync_DeletesBlobAndJob_ForTerminalJobsOlderThanCutoff()
    {
        var repo = new Mock<IEmailJobRepository>(MockBehavior.Strict);
        var store = new Mock<IDocumentFileStore>(MockBehavior.Strict);

        var oldJob = new EmailJob
        {
            Id = Guid.NewGuid(),
            Status = EmailJobStatus.Completed,
            CreatedUtc = DateTime.UtcNow.AddDays(-60),
            ContentBlobPath = $"email-jobs/{Guid.NewGuid():D}.html"
        };

        repo.Setup(r => r.GetTerminalJobsOlderThanAsync(It.IsAny<DateTime>(), 25))
            .ReturnsAsync(new List<EmailJob> { oldJob });
        store.Setup(s => s.DeleteAsync(oldJob.ContentBlobPath)).Returns(Task.CompletedTask);
        repo.Setup(r => r.DeleteAsync(oldJob.Id)).Returns(Task.CompletedTask);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EmailJobs:RetentionDays"] = "30",
                ["EmailJobs:CleanupBatchSize"] = "25"
            })
            .Build();

        var svc = new EmailJobCleanupService(repo.Object, store.Object, config, NullLogger<EmailJobCleanupService>.Instance);
        await svc.RunOnceBestEffortAsync();

        store.Verify(s => s.DeleteAsync(oldJob.ContentBlobPath), Times.Once);
        repo.Verify(r => r.DeleteAsync(oldJob.Id), Times.Once);
        repo.Verify(r => r.GetTerminalJobsOlderThanAsync(It.IsAny<DateTime>(), 25), Times.Once);
    }

    [Fact]
    public async Task RunOnceBestEffortAsync_DoesNotThrow_WhenBlobDeleteFails_DoesNotDeleteJobRow()
    {
        var repo = new Mock<IEmailJobRepository>(MockBehavior.Strict);
        var store = new Mock<IDocumentFileStore>(MockBehavior.Strict);

        var oldJob = new EmailJob
        {
            Id = Guid.NewGuid(),
            Status = EmailJobStatus.Failed,
            CreatedUtc = DateTime.UtcNow.AddDays(-60),
            ContentBlobPath = $"email-jobs/{Guid.NewGuid():D}.html"
        };

        repo.Setup(r => r.GetTerminalJobsOlderThanAsync(It.IsAny<DateTime>(), 25))
            .ReturnsAsync(new List<EmailJob> { oldJob });
        store.Setup(s => s.DeleteAsync(oldJob.ContentBlobPath)).ThrowsAsync(new InvalidOperationException("boom"));

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EmailJobs:RetentionDays"] = "30",
                ["EmailJobs:CleanupBatchSize"] = "25"
            })
            .Build();

        var svc = new EmailJobCleanupService(repo.Object, store.Object, config, NullLogger<EmailJobCleanupService>.Instance);
        await svc.RunOnceBestEffortAsync();

        repo.Verify(r => r.GetTerminalJobsOlderThanAsync(It.IsAny<DateTime>(), 25), Times.Once);
        store.Verify(s => s.DeleteAsync(oldJob.ContentBlobPath), Times.Once);
        repo.Verify(r => r.DeleteAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task RunOnceBestEffortAsync_UsesConfiguredBatchSize()
    {
        var repo = new Mock<IEmailJobRepository>(MockBehavior.Strict);
        var store = new Mock<IDocumentFileStore>(MockBehavior.Strict);

        repo.Setup(r => r.GetTerminalJobsOlderThanAsync(It.IsAny<DateTime>(), 3))
            .ReturnsAsync(new List<EmailJob>());

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EmailJobs:RetentionDays"] = "30",
                ["EmailJobs:CleanupBatchSize"] = "3"
            })
            .Build();

        var svc = new EmailJobCleanupService(repo.Object, store.Object, config, NullLogger<EmailJobCleanupService>.Instance);
        await svc.RunOnceBestEffortAsync();

        repo.Verify(r => r.GetTerminalJobsOlderThanAsync(It.IsAny<DateTime>(), 3), Times.Once);
    }
}

