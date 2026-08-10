using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Web.Configuration;
using Web.MockData;
using Web.Services;
using Web.Services.Repositories;
using Xunit;

namespace Web.UnitTests;

public sealed class PostmarkSuppressionSyncServiceTests
{
    private static (PostmarkSuppressionSyncService Service, Mock<IPostmarkSuppressionDumpClient> DumpClient) Create(
        PostmarkSuppressionSyncOptions options,
        string serverToken,
        string environmentName,
        TaskCompletionSource? ranSignal = null,
        ILogger<PostmarkSuppressionSyncService>? logger = null
    )
    {
        var dumpClient = new Mock<IPostmarkSuppressionDumpClient>();
        dumpClient
            .Setup(c => c.GetSuppressionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PostmarkSuppressionDumpEntry>())
            .Callback(() => ranSignal?.TrySetResult());

        var services = new ServiceCollection();
        services.AddSingleton(dumpClient.Object);
        services.AddSingleton<IEmailSuppressionRepository>(new MockEmailSuppressionRepository());
        services.AddScoped<IEmailSuppressionService, EmailSuppressionService>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IAuditLogRepository>(new Mock<IAuditLogRepository>().Object);
        services.AddSingleton(Options.Create(new PostmarkOptions { ServerToken = serverToken }));
        services.AddSingleton<ILogger<PostmarkSuppressionSyncRunner>>(
            NullLogger<PostmarkSuppressionSyncRunner>.Instance
        );
        services.AddScoped<PostmarkSuppressionSyncRunner>();

        var env = new Mock<IHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns(environmentName);

        var provider = services.BuildServiceProvider();
        var service = new PostmarkSuppressionSyncService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(options),
            Options.Create(new PostmarkOptions { ServerToken = serverToken }),
            env.Object,
            logger ?? NullLogger<PostmarkSuppressionSyncService>.Instance
        );

        return (service, dumpClient);
    }

    private static PostmarkSuppressionSyncOptions Enabled() =>
        new() { Enabled = true, IntervalHours = 24, StartupDelaySeconds = 0 };

    [Fact]
    public async Task Runs_immediately_on_startup_rather_than_waiting_out_the_interval()
    {
        // The UserPurgeService invariant: the loop runs before it waits. If it delayed first, a
        // deploy cadence shorter than IntervalHours would silently mean the reconciliation never
        // runs. Running more often is free: reruns are evidence-key no-ops.
        var ran = new TaskCompletionSource();
        var (service, dumpClient) = Create(Enabled(), "token", "Production", ran);

        await service.StartAsync(CancellationToken.None);
        var completed = await Task.WhenAny(ran.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        await service.StopAsync(CancellationToken.None);

        Assert.Same(ran.Task, completed);
        dumpClient.Verify(
            c => c.GetSuppressionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce
        );
    }

    [Fact]
    public async Task Does_not_run_when_disabled()
    {
        var ran = new TaskCompletionSource();
        var options = Enabled();
        options.Enabled = false;
        var (service, dumpClient) = Create(options, "token", "Production", ran);

        await service.StartAsync(CancellationToken.None);
        var completed = await Task.WhenAny(ran.Task, Task.Delay(TimeSpan.FromMilliseconds(250)));
        await service.StopAsync(CancellationToken.None);

        Assert.NotSame(ran.Task, completed);
        dumpClient.Verify(
            c => c.GetSuppressionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    [Fact]
    public async Task Does_not_run_when_enabled_without_a_server_token_outside_mockdata()
    {
        // Fail fast and log once, rather than throwing on every interval: the dump API
        // authenticates with the same server token the SMTP transport uses, and a sync without
        // it can never work.
        var ran = new TaskCompletionSource();
        var (service, dumpClient) = Create(Enabled(), "", "Production", ran);

        await service.StartAsync(CancellationToken.None);
        var completed = await Task.WhenAny(ran.Task, Task.Delay(TimeSpan.FromMilliseconds(250)));
        await service.StopAsync(CancellationToken.None);

        Assert.NotSame(ran.Task, completed);
        dumpClient.Verify(
            c => c.GetSuppressionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    [Fact]
    public async Task Runs_without_a_server_token_in_mockdata_where_the_dump_client_is_a_fake()
    {
        var ran = new TaskCompletionSource();
        var (service, _) = Create(Enabled(), "", "MockData", ran);

        await service.StartAsync(CancellationToken.None);
        var completed = await Task.WhenAny(ran.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        await service.StopAsync(CancellationToken.None);

        Assert.Same(ran.Task, completed);
    }

    [Fact]
    public async Task A_failed_run_is_logged_and_swallowed_so_the_loop_and_host_survive()
    {
        // An exception escaping BackgroundService.ExecuteAsync stops the whole host under the
        // default BackgroundServiceExceptionBehavior, so the catch-all around the run is
        // load-bearing: a throwing dump fetch must surface as an Error log, nothing more.
        var logger = new ListLogger<PostmarkSuppressionSyncService>();
        var ran = new TaskCompletionSource();
        var (service, dumpClient) = Create(Enabled(), "token", "Production", ran, logger);
        dumpClient
            .Setup(c => c.GetSuppressionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => ran.TrySetResult())
            .ThrowsAsync(new HttpRequestException("Postmark unreachable"));

        await service.StartAsync(CancellationToken.None);
        await Task.WhenAny(ran.Task, Task.Delay(TimeSpan.FromSeconds(10)));

        var sawError = await WaitForAsync(
            () => logger.Entries.Any(e => e.Level == LogLevel.Error && e.Message.Contains("Unhandled error")),
            TimeSpan.FromSeconds(10)
        );
        await service.StopAsync(CancellationToken.None);

        Assert.True(sawError, "The failed run was not logged at Error (or escaped ExecuteAsync).");
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(25);
        }
        return condition();
    }

    /// <summary>Captures log entries for assertions the NullLogger can't support.</summary>
    private sealed class ListLogger<T> : ILogger<T>
    {
        private readonly object _gate = new();
        private readonly List<(LogLevel Level, string Message)> _entries = new();

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get
            {
                lock (_gate)
                    return _entries.ToList();
            }
        }

        public IDisposable BeginScope<TState>(TState state) => new NoopDisposable();

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            lock (_gate)
                _entries.Add((logLevel, formatter(state, exception)));
        }

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }
}
