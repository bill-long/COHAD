using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Web.Configuration;
using Web.MockData;
using Web.Models;
using Web.Services;
using Web.Services.Repositories;
using Xunit;

namespace Web.UnitTests;

public sealed class PostmarkSuppressionSyncRunnerTests
{
    private sealed class Harness
    {
        public MockEmailSuppressionRepository Repository { get; } = new();
        public TestClock Clock { get; } = new();
        public Mock<IAuditLogRepository> AuditLog { get; } = new();
        public StubDumpClient DumpClient { get; } = new();
        public ListLogger<PostmarkSuppressionSyncRunner> Logs { get; } = new();
        public EmailSuppressionService SuppressionService { get; }
        public PostmarkSuppressionSyncRunner Runner { get; }

        public Harness(PostmarkOptions? postmarkOptions = null, IEmailSuppressionService? suppressionService = null)
        {
            SuppressionService = new EmailSuppressionService(Repository, Clock);
            Runner = new PostmarkSuppressionSyncRunner(
                DumpClient,
                Repository,
                suppressionService ?? SuppressionService,
                AuditLog.Object,
                Options.Create(
                    postmarkOptions
                        ?? new PostmarkOptions { BroadcastStream = "broadcast", TransactionalStream = "outbound" }
                ),
                Logs
            );
        }

        public List<NewAuditLogEntry> AuditEntries =>
            AuditLog.Invocations.Select(i => (NewAuditLogEntry)i.Arguments[0]).ToList();

        public Task<EmailSuppression?> GetAsync(string email) => Repository.GetByEmailAsync(email);
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

    /// <summary>Per-stream canned dump, recording which streams were queried.</summary>
    private sealed class StubDumpClient : IPostmarkSuppressionDumpClient
    {
        private readonly Dictionary<string, IReadOnlyList<PostmarkSuppressionDumpEntry>> _dumps =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, Exception> _faults = new(StringComparer.Ordinal);

        public List<string> QueriedStreams { get; } = new();

        public StubDumpClient WithDump(string stream, params PostmarkSuppressionDumpEntry[] entries)
        {
            _dumps[stream] = entries;
            return this;
        }

        public StubDumpClient ThrowForStream(string stream, Exception exception)
        {
            _faults[stream] = exception;
            return this;
        }

        public Task<IReadOnlyList<PostmarkSuppressionDumpEntry>> GetSuppressionsAsync(
            string messageStream,
            CancellationToken cancellationToken
        )
        {
            QueriedStreams.Add(messageStream);
            if (_faults.TryGetValue(messageStream, out var fault))
                throw fault;
            return Task.FromResult(
                _dumps.TryGetValue(messageStream, out var entries)
                    ? entries
                    : (IReadOnlyList<PostmarkSuppressionDumpEntry>)Array.Empty<PostmarkSuppressionDumpEntry>()
            );
        }
    }

    private static PostmarkSuppressionDumpEntry DumpEntry(
        string email,
        string reason = "ManualSuppression",
        string origin = "Recipient"
    ) =>
        new()
        {
            EmailAddress = email,
            SuppressionReason = reason,
            Origin = origin,
            CreatedAt = "2026-08-01T12:00:00-05:00",
        };

    [Fact]
    public async Task Records_a_dumped_manual_suppression_as_provider_unsubscribe()
    {
        var h = new Harness();
        h.DumpClient.WithDump("broadcast", DumpEntry("Resident@Example.com"));

        var result = await h.Runner.RunAsync(CancellationToken.None);

        Assert.Equal(1, result.Recorded);
        var suppression = await h.GetAsync("resident@example.com");
        Assert.NotNull(suppression);
        Assert.True(suppression.IsActive);
        Assert.Equal(SuppressionReason.ProviderUnsubscribe, suppression.Reason);
        Assert.Equal(EmailSuppression.PostmarkSuppressionDump, suppression.SuppressedBy);
        Assert.Equal(1, suppression.ConsecutiveFailureCount);
        Assert.Equal(
            "postmark:suppression-dump:broadcast:resident@example.com",
            suppression.LastEvidenceKey
        );
        // The record explains itself: stream, origin, and Postmark's own reason are on it.
        Assert.Equal(
            "Postmark stream suppression (broadcast stream; Origin: Recipient; SuppressionReason: ManualSuppression)",
            suppression.ProviderDiagnostic
        );

        var audit = Assert.Single(h.AuditEntries);
        Assert.Equal("system", audit.UserId);
        Assert.Contains("unsubscribe via the email provider", audit.Action);
        Assert.Contains("broadcast", audit.Action);
        // The audit log never carries the full address.
        Assert.DoesNotContain("resident@example.com", audit.Action);
    }

    [Theory]
    [InlineData("HardBounce", SuppressionReason.HardBounce)]
    [InlineData("SpamComplaint", SuppressionReason.SpamComplaint)]
    public async Task Maps_bounce_and_complaint_reasons_to_the_matching_cohad_reason(
        string postmarkReason,
        SuppressionReason expected
    )
    {
        var h = new Harness();
        h.DumpClient.WithDump("broadcast", DumpEntry("a@example.com", postmarkReason));

        await h.Runner.RunAsync(CancellationToken.None);

        var suppression = await h.GetAsync("a@example.com");
        Assert.Equal(expected, suppression!.Reason);
    }

    [Theory]
    [InlineData("SomeFutureReason")]
    [InlineData(null)]
    public async Task An_unrecognized_reason_records_as_provider_unsubscribe_because_the_address_is_suppressed(
        string? postmarkReason
    )
    {
        var h = new Harness();
        h.DumpClient.WithDump("broadcast", DumpEntry("a@example.com", postmarkReason!));

        await h.Runner.RunAsync(CancellationToken.None);

        var suppression = await h.GetAsync("a@example.com");
        Assert.Equal(SuppressionReason.ProviderUnsubscribe, suppression!.Reason);
    }

    [Fact]
    public async Task Queries_both_configured_streams()
    {
        var h = new Harness();
        h.DumpClient
            .WithDump("broadcast", DumpEntry("blast@example.com"))
            .WithDump("outbound", DumpEntry("txn@example.com"));

        var result = await h.Runner.RunAsync(CancellationToken.None);

        Assert.Equal(new[] { "broadcast", "outbound" }, h.DumpClient.QueriedStreams);
        Assert.Equal(2, result.Recorded);
        Assert.NotNull(await h.GetAsync("blast@example.com"));
        Assert.NotNull(await h.GetAsync("txn@example.com"));
    }

    [Fact]
    public async Task Both_options_naming_the_same_stream_queries_it_once()
    {
        var h = new Harness(
            new PostmarkOptions { BroadcastStream = "broadcast", TransactionalStream = "broadcast" }
        );
        h.DumpClient.WithDump("broadcast", DumpEntry("a@example.com"));

        var result = await h.Runner.RunAsync(CancellationToken.None);

        Assert.Equal(new[] { "broadcast" }, h.DumpClient.QueriedStreams);
        Assert.Equal(1, result.StreamsQueried);
    }

    [Fact]
    public async Task Skips_an_address_already_actively_suppressed_without_new_evidence_or_audit()
    {
        var h = new Harness();
        await h.SuppressionService.RecordAsync(
            "a@example.com",
            SuppressionReason.AdminAction,
            "admin-1",
            null,
            null
        );
        h.DumpClient.WithDump("broadcast", DumpEntry("a@example.com"));

        var result = await h.Runner.RunAsync(CancellationToken.None);

        Assert.Equal(0, result.Recorded);
        Assert.Equal(1, result.AlreadySuppressed);
        var suppression = await h.GetAsync("a@example.com");
        // Untouched: still the admin's record, still one piece of evidence.
        Assert.Equal(SuppressionReason.AdminAction, suppression!.Reason);
        Assert.Equal(1, suppression.ConsecutiveFailureCount);
        Assert.Empty(h.AuditEntries);
    }

    [Fact]
    public async Task A_rerun_is_a_noop_per_address()
    {
        var h = new Harness();
        h.DumpClient.WithDump("broadcast", DumpEntry("a@example.com"));

        await h.Runner.RunAsync(CancellationToken.None);
        var second = await h.Runner.RunAsync(CancellationToken.None);

        Assert.Equal(0, second.Recorded);
        var suppression = await h.GetAsync("a@example.com");
        Assert.Equal(1, suppression!.ConsecutiveFailureCount);
        Assert.Single(h.AuditEntries);
    }

    [Fact]
    public async Task An_address_dumped_on_both_streams_is_recorded_once()
    {
        var h = new Harness();
        h.DumpClient
            .WithDump("broadcast", DumpEntry("a@example.com"))
            .WithDump("outbound", DumpEntry("a@example.com"));

        var result = await h.Runner.RunAsync(CancellationToken.None);

        Assert.Equal(1, result.Recorded);
        Assert.Equal(1, result.AlreadySuppressed);
        var suppression = await h.GetAsync("a@example.com");
        Assert.Equal(1, suppression!.ConsecutiveFailureCount);
        Assert.Single(h.AuditEntries);
    }

    [Fact]
    public async Task Reconciliation_never_clears_a_cohad_suppression()
    {
        // An address actively suppressed here but absent from the dump stays suppressed: a clear
        // is a deliberate act, and an absent dump entry can mean a reactivation the webhook
        // already mirrored. Additive only is the issue's own invariant.
        var h = new Harness();
        await h.SuppressionService.RecordAsync(
            "keep@example.com",
            SuppressionReason.HardBounce,
            EmailSuppression.SystemDeliveryEvent,
            null,
            "HardBounce: mailbox closed"
        );
        h.DumpClient.WithDump("broadcast", DumpEntry("other@example.com"));

        await h.Runner.RunAsync(CancellationToken.None);

        var suppression = await h.GetAsync("keep@example.com");
        Assert.True(suppression!.IsActive);
        Assert.Null(suppression.ClearedUtc);
        Assert.Equal(1, suppression.ConsecutiveFailureCount);
    }

    [Fact]
    public async Task Resuppresses_an_address_cleared_only_in_cohad_while_postmark_still_suppresses_it()
    {
        // The documented clear procedure is two-system. Until the Postmark-side reactivation
        // happens, the address's mail is still silently dropped, so the next run puts the
        // suppression back - the truthful state, not a fight with the admin.
        var h = new Harness();
        await h.SuppressionService.RecordAsync(
            "a@example.com",
            SuppressionReason.ProviderUnsubscribe,
            EmailSuppression.PostmarkSubscriptionChange,
            null,
            null
        );
        await h.SuppressionService.ClearAsync("a@example.com", "admin-1");
        h.DumpClient.WithDump("broadcast", DumpEntry("a@example.com"));

        var result = await h.Runner.RunAsync(CancellationToken.None);

        Assert.Equal(1, result.Recorded);
        var suppression = await h.GetAsync("a@example.com");
        Assert.True(suppression!.IsActive);
        Assert.Null(suppression.ClearedUtc);
        Assert.Equal(SuppressionReason.ProviderUnsubscribe, suppression.Reason);
        Assert.Equal(EmailSuppression.PostmarkSuppressionDump, suppression.SuppressedBy);
        Assert.Equal(2, suppression.ConsecutiveFailureCount);
    }

    [Fact]
    public async Task A_failed_record_write_does_not_lose_the_rest_of_the_dump()
    {
        var service = new Mock<IEmailSuppressionService>();
        service
            .Setup(s =>
                s.RecordAsync(
                    "bad@example.com",
                    It.IsAny<SuppressionReason>(),
                    It.IsAny<string>(),
                    It.IsAny<Guid?>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<DateTime?>()
                )
            )
            .ThrowsAsync(new ConcurrencyConflictException("lost every race", new Exception()));
        var h = new Harness(suppressionService: service.Object);
        // The other address records through the real service, so only the failing write errors.
        service
            .Setup(s =>
                s.RecordAsync(
                    "good@example.com",
                    It.IsAny<SuppressionReason>(),
                    It.IsAny<string>(),
                    It.IsAny<Guid?>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<DateTime?>()
                )
            )
            .Returns(
                (string email, SuppressionReason reason, string by, Guid? job, string? diagnostic, string? key, DateTime? eventUtc) =>
                    h.SuppressionService.RecordAsync(email, reason, by, job, diagnostic, key, eventUtc)
            );
        h.DumpClient.WithDump(
            "broadcast",
            DumpEntry("bad@example.com"),
            DumpEntry("good@example.com")
        );

        var result = await h.Runner.RunAsync(CancellationToken.None);

        Assert.Equal(1, result.Errors);
        Assert.NotNull(await h.GetAsync("good@example.com"));
    }

    [Fact]
    public async Task The_records_suppressed_date_is_the_providers_created_at_not_the_run_time()
    {
        // The admin reads SUPPRESSED as "when did this address stop getting mail" - for a
        // reconciled record that is Postmark's CreatedAt, not when the sync happened to run.
        var h = new Harness();
        h.DumpClient.WithDump("broadcast", DumpEntry("a@example.com"));

        await h.Runner.RunAsync(CancellationToken.None);

        var suppression = await h.GetAsync("a@example.com");
        Assert.Equal(new DateTime(2026, 8, 1, 17, 0, 0, DateTimeKind.Utc), suppression!.SuppressedUtc);
        // ...while first/last-seen stay honest about when the evidence arrived here.
        Assert.Equal(h.Clock.GetUtcNow().UtcDateTime, suppression.FirstSeenUtc);
        Assert.Equal(h.Clock.GetUtcNow().UtcDateTime, suppression.LastSeenUtc);
    }

    [Fact]
    public async Task An_entry_without_created_at_suppresses_at_the_run_time()
    {
        var h = new Harness();
        var entry = DumpEntry("a@example.com");
        entry.CreatedAt = null;
        h.DumpClient.WithDump("broadcast", entry);

        await h.Runner.RunAsync(CancellationToken.None);

        var suppression = await h.GetAsync("a@example.com");
        Assert.Equal(h.Clock.GetUtcNow().UtcDateTime, suppression!.SuppressedUtc);
    }

    [Theory]
    [InlineData("2019-12-17T08:58:33-05:00", "2019-12-17T13:58:33Z")]
    [InlineData("2026-08-01T12:00:00Z", "2026-08-01T12:00:00Z")]
    // Offset-less input is read as UTC, not host-local time.
    [InlineData("2026-08-01T12:00:00", "2026-08-01T12:00:00Z")]
    [InlineData("not-a-date", null)]
    [InlineData(null, null)]
    [InlineData("", null)]
    public void Parse_created_at_is_lenient(string? createdAt, string? expectedUtc)
    {
        var parsed = new Harness().Runner.ParseCreatedAt(createdAt);

        if (expectedUtc == null)
        {
            Assert.Null(parsed);
        }
        else
        {
            Assert.Equal(DateTimeOffset.Parse(expectedUtc).UtcDateTime, parsed);
        }
    }

    [Fact]
    public void An_unparseable_created_at_warns_with_the_offending_value()
    {
        var h = new Harness();

        h.Runner.ParseCreatedAt("not-a-date");

        Assert.Contains(
            h.Logs.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("not-a-date")
        );
    }

    [Fact]
    public void An_absent_created_at_does_not_warn()
    {
        var h = new Harness();

        h.Runner.ParseCreatedAt(null);
        h.Runner.ParseCreatedAt("   ");

        Assert.DoesNotContain(h.Logs.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task A_failed_dump_read_aborts_the_run_and_propagates()
    {
        // The hosted service's catch-all logs this and the next interval retries. Reconciling
        // with half the picture risks nothing (the pass is additive), but a silently skipped
        // stream would make the summary log lie - so the failure must ESCAPE RunAsync, not be
        // swallowed into a false "dumped=0" summary.
        var h = new Harness();
        h.DumpClient.ThrowForStream("broadcast", new HttpRequestException("Postmark unreachable"));
        h.DumpClient.WithDump("outbound", DumpEntry("a@example.com"));

        await Assert.ThrowsAsync<HttpRequestException>(() => h.Runner.RunAsync(CancellationToken.None));

        // Nothing was recorded, and the outbound stream was never reached.
        Assert.Equal(new[] { "broadcast" }, h.DumpClient.QueriedStreams);
        Assert.Null(await h.GetAsync("a@example.com"));
        Assert.Empty(h.AuditEntries);
    }

    [Fact]
    public async Task A_failed_second_stream_keeps_the_first_streams_records_and_propagates()
    {
        var h = new Harness();
        h.DumpClient.WithDump("broadcast", DumpEntry("kept@example.com"));
        h.DumpClient.ThrowForStream("outbound", new HttpRequestException("Postmark unreachable"));

        await Assert.ThrowsAsync<HttpRequestException>(() => h.Runner.RunAsync(CancellationToken.None));

        // The broadcast records are durable; the next run reconciles the rest (evidence-key
        // no-ops make that retry free).
        Assert.NotNull(await h.GetAsync("kept@example.com"));
    }

    [Fact]
    public async Task A_blank_entry_is_skipped_without_counting_as_dumped()
    {
        // Defense in depth behind the client's own blank-entry skip (the runner must be safe
        // against any IPostmarkSuppressionDumpClient, including the MockData fake).
        var h = new Harness();
        h.DumpClient.WithDump("broadcast", DumpEntry("   "), DumpEntry("real@example.com"));

        var result = await h.Runner.RunAsync(CancellationToken.None);

        Assert.Equal(1, result.DumpedAddresses);
        Assert.Equal(1, result.Recorded);
        Assert.NotNull(await h.GetAsync("real@example.com"));
    }

    [Fact]
    public async Task An_audit_failure_after_an_applied_write_keeps_the_record_and_is_not_a_write_error()
    {
        // The record IS durable; only the audit entry is lost. The failure must not read as
        // "failed to record" in the summary counters.
        var h = new Harness();
        h.AuditLog
            .Setup(r => r.AddAsync(It.IsAny<NewAuditLogEntry>()))
            .ThrowsAsync(new InvalidOperationException("audit store down"));
        h.DumpClient.WithDump("broadcast", DumpEntry("a@example.com"));

        var result = await h.Runner.RunAsync(CancellationToken.None);

        Assert.Equal(1, result.Recorded);
        Assert.Equal(0, result.Errors);
        var suppression = await h.GetAsync("a@example.com");
        Assert.True(suppression!.IsActive);
    }

    [Fact]
    public async Task Audits_nothing_for_a_run_that_records_nothing()
    {
        var h = new Harness();
        h.DumpClient.WithDump("broadcast");

        var result = await h.Runner.RunAsync(CancellationToken.None);

        Assert.Equal(0, result.Recorded);
        Assert.Empty(h.AuditEntries);
    }

    [Fact]
    public void The_evidence_key_is_deterministic_over_stream_and_normalized_address()
    {
        Assert.Equal(
            PostmarkSuppressionSyncRunner.MakeEvidenceKey("broadcast", "a@example.com"),
            PostmarkSuppressionSyncRunner.MakeEvidenceKey("broadcast", "a@example.com")
        );
        Assert.NotEqual(
            PostmarkSuppressionSyncRunner.MakeEvidenceKey("broadcast", "a@example.com"),
            PostmarkSuppressionSyncRunner.MakeEvidenceKey("outbound", "a@example.com")
        );
    }
}
