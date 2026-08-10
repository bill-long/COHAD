#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Web.Configuration;
using Web.Models;
using Web.Services.Repositories;

namespace Web.Services
{
    /// <summary>
    /// Configuration for <see cref="PostmarkSuppressionSyncService"/> / <see cref="PostmarkSuppressionSyncRunner"/>.
    /// </summary>
    public sealed class PostmarkSuppressionSyncOptions
    {
        public const string SectionName = "Postmark:SuppressionSync";

        /// <summary>Master switch. Off by default; enabled in production via app setting.</summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// How long <see cref="PostmarkSuppressionSyncService"/> waits between reconciliations.
        /// Consumed by the hosted service, not by the runner itself.
        /// </summary>
        public int IntervalHours { get; set; } = 24;

        /// <summary>
        /// Delay before the first run after startup, so the app finishes booting first. Consumed
        /// by the hosted service; tests set it to zero.
        /// </summary>
        public int StartupDelaySeconds { get; set; } = 15;
    }

    /// <summary>What one reconciliation run did.</summary>
    public sealed class PostmarkSuppressionSyncResult
    {
        public int StreamsQueried { get; set; }

        /// <summary>Usable addresses the dumps carried (after the client's blank-entry skips).</summary>
        public int DumpedAddresses { get; set; }

        /// <summary>Dumped addresses already actively suppressed in COHAD - left untouched.</summary>
        public int AlreadySuppressed { get; set; }

        /// <summary>Addresses this run newly recorded (or re-suppressed after a clear).</summary>
        public int Recorded { get; set; }

        /// <summary>Dumped addresses whose record write failed. The run continues past them.</summary>
        public int Errors { get; set; }
    }

    /// <summary>
    /// One reconciliation pass against Postmark's suppression dumps (issue #9). The
    /// SubscriptionChange webhook mirrors Postmark-layer suppressions going forward, but anything
    /// suppressed at the Postmark layer while the webhook trigger was not configured is invisible
    /// to COHAD - and Postmark silently drops mail to those addresses after accepting the send.
    /// This pass reads both streams' suppression dumps and records a COHAD suppression for any
    /// dumped address not already actively suppressed here.
    /// <para>
    /// Additive only, by design: it never clears a COHAD suppression. A clear is a deliberate act,
    /// and an absent dump entry can also mean a reactivation the webhook already mirrored.
    /// </para>
    /// <para>
    /// Idempotent per dumped address: the evidence key is deterministic over (stream, address), so
    /// a rerun finds the active record already carrying the key and applies nothing - no count
    /// inflation, no duplicate audit entries, exactly like the webhook writers. One deliberate
    /// consequence: an address an admin cleared ONLY in COHAD (without reactivating it in Postmark,
    /// the documented two-system clear procedure) is re-suppressed by the next run, because
    /// Postmark still silently drops its mail - the re-suppression is the truthful state.
    /// </para>
    /// </summary>
    public sealed class PostmarkSuppressionSyncRunner
    {
        private readonly IPostmarkSuppressionDumpClient _dumpClient;
        private readonly IEmailSuppressionRepository _repository;
        private readonly IEmailSuppressionService _suppressionService;
        private readonly IAuditLogRepository _auditLogRepository;
        private readonly PostmarkOptions _postmarkOptions;
        private readonly ILogger<PostmarkSuppressionSyncRunner> _logger;

        public PostmarkSuppressionSyncRunner(
            IPostmarkSuppressionDumpClient dumpClient,
            IEmailSuppressionRepository repository,
            IEmailSuppressionService suppressionService,
            IAuditLogRepository auditLogRepository,
            IOptions<PostmarkOptions> postmarkOptions,
            ILogger<PostmarkSuppressionSyncRunner> logger
        )
        {
            _dumpClient = dumpClient;
            _repository = repository;
            _suppressionService = suppressionService;
            _auditLogRepository = auditLogRepository;
            _postmarkOptions = postmarkOptions.Value;
            _logger = logger;
        }

        /// <summary>
        /// The evidence key for a dump entry: deterministic over (stream, normalized address), so
        /// a rerun of the reconciliation against an unchanged dump is a no-op per address. The
        /// stream is part of the key because the same address can legitimately appear on both
        /// streams - though the runner's active-set dedup means only the first sighting writes.
        /// </summary>
        internal static string MakeEvidenceKey(string messageStream, string normalizedEmail) =>
            $"{EmailSuppression.PostmarkSuppressionDump}:{messageStream}:{normalizedEmail}";

        /// <summary>
        /// Maps Postmark's <c>SuppressionReason</c> to the COHAD reason. Anything else - including
        /// a future value - records as <see cref="SuppressionReason.ProviderUnsubscribe"/>: the
        /// address IS on Postmark's suppression list, so "do not mail" is the only safe reading,
        /// and the dump's own reason text is preserved on the record's diagnostic either way.
        /// </summary>
        internal static SuppressionReason MapReason(string? postmarkSuppressionReason) =>
            postmarkSuppressionReason switch
            {
                "HardBounce" => SuppressionReason.HardBounce,
                "SpamComplaint" => SuppressionReason.SpamComplaint,
                _ => SuppressionReason.ProviderUnsubscribe,
            };

        /// <summary>
        /// Parses the dump entry's <c>CreatedAt</c> (ISO 8601 with offset, e.g.
        /// "2019-12-17T08:58:33-05:00") so the record's <c>SuppressedUtc</c> can say when the
        /// address actually stopped receiving mail. Lenient like the rest of the dump handling:
        /// an absent or unparseable value is no timestamp at all, never a failed run - but a
        /// NON-EMPTY unparseable value warns, so a provider payload format regression is
        /// attributable rather than silently stamping the run time. Offset-less input is read
        /// as UTC, deterministically, rather than as host-local time.
        /// </summary>
        internal DateTime? ParseCreatedAt(string? createdAt)
        {
            if (string.IsNullOrWhiteSpace(createdAt))
                return null;

            if (
                DateTimeOffset.TryParse(
                    createdAt,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal
                        | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var parsed
                )
            )
            {
                return parsed.UtcDateTime;
            }

            _logger.LogWarning(
                "Postmark suppression dump entry has an unparseable CreatedAt {CreatedAt} - stamping the suppression with the run time.",
                createdAt
            );
            return null;
        }

        public async Task<PostmarkSuppressionSyncResult> RunAsync(CancellationToken cancellationToken)
        {
            var result = new PostmarkSuppressionSyncResult();

            // Both streams, deduped: a misconfiguration pointing both at the same stream must not
            // reconcile it twice (the second pass would be a no-op anyway, but the log would lie).
            var streams = new List<string>();
            foreach (var stream in new[] { _postmarkOptions.BroadcastStream, _postmarkOptions.TransactionalStream })
            {
                if (!string.IsNullOrWhiteSpace(stream) && !streams.Contains(stream, StringComparer.Ordinal))
                    streams.Add(stream);
            }

            // One read of the COHAD list per run; the set is updated as records are written below,
            // so an address dumped on BOTH streams is recorded once, not once per stream.
            var active = EmailSuppression.ActiveNormalizedAddressSet(await _repository.GetActiveAsync());

            foreach (var stream in streams)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.StreamsQueried++;

                // A failed dump read aborts the run (the hosted service logs it and the next
                // interval retries): reconciling with half the picture risks nothing - the pass is
                // additive - but a silently skipped stream would make the summary log lie.
                var entries = await _dumpClient.GetSuppressionsAsync(stream, cancellationToken);

                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var normalized = EmailSuppression.NormalizeAddress(entry.EmailAddress);
                    if (normalized.Length == 0)
                        continue;

                    result.DumpedAddresses++;

                    if (active.Contains(normalized))
                    {
                        result.AlreadySuppressed++;
                        continue;
                    }

                    var reason = MapReason(entry.SuppressionReason);
                    if (
                        reason == SuppressionReason.ProviderUnsubscribe
                        && !string.Equals(entry.SuppressionReason, "ManualSuppression", StringComparison.Ordinal)
                    )
                    {
                        _logger.LogWarning(
                            "Postmark suppression dump entry for {Email} ({Stream} stream) has unrecognized SuppressionReason {SuppressionReason} - recording as ProviderUnsubscribe.",
                            entry.EmailAddress,
                            stream,
                            entry.SuppressionReason ?? "(absent)"
                        );
                    }

                    var diagnostic = EmailDeliveryActionService.BuildSubscriptionChangeDiagnostic(
                        entry.Origin,
                        entry.SuppressionReason,
                        stream
                    );

                    try
                    {
                        var outcome = await _suppressionService.RecordAsync(
                            entry.EmailAddress,
                            reason,
                            EmailSuppression.PostmarkSuppressionDump,
                            null,
                            diagnostic,
                            evidenceKey: MakeEvidenceKey(stream, normalized),
                            eventUtc: ParseCreatedAt(entry.CreatedAt)
                        );

                        // The record is in force either way; only a write THIS call performed is
                        // audited (a rerun's replayed evidence changes nothing, and a second
                        // audit line would misstate how often the address failed).
                        if (outcome.Applied)
                        {
                            active.Add(normalized);
                            result.Recorded++;
                            try
                            {
                                await AuditAsync(entry, stream, reason, outcome.Suppression);
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                // The record IS durable; only its audit entry is lost, and the
                                // evidence key means the next run will not retry it. Error so the
                                // gap is attributable - and NOT counted in result.Errors, which
                                // reports record-write failures, not audit ones. (The sibling
                                // writers lose their audit the same way when AddAsync throws.)
                                _logger.LogError(
                                    ex,
                                    "Recorded a suppression for {Email} from the {Stream} stream dump, but writing its audit entry failed.",
                                    entry.EmailAddress,
                                    stream
                                );
                            }
                        }
                        else
                        {
                            // Replayed evidence: the address is suppressed, so treat later
                            // streams' sightings of it as already-handled too.
                            active.Add(normalized);
                            result.AlreadySuppressed++;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // One bad record write must not lose the rest of the dump. The address is
                        // deliberately NOT added to the active set: if it appears on the other
                        // stream too, that sighting is another chance to record it.
                        result.Errors++;
                        _logger.LogError(
                            ex,
                            "Failed to record a suppression for {Email} from the {Stream} stream dump.",
                            entry.EmailAddress,
                            stream
                        );
                    }
                }
            }

            return result;
        }

        private async Task AuditAsync(
            PostmarkSuppressionDumpEntry entry,
            string stream,
            SuppressionReason reason,
            EmailSuppression suppression
        )
        {
            var reasonText = reason switch
            {
                SuppressionReason.HardBounce => "hard bounce",
                SuppressionReason.SpamComplaint => "spam report",
                _ => "unsubscribe via the email provider",
            };

            var redacted = EmailDeliveryActionService.RedactEmail(suppression.Email);
            await _auditLogRepository.AddAsync(
                new NewAuditLogEntry
                {
                    Id = Guid.NewGuid(),
                    Time = DateTime.UtcNow,
                    UserId = "system",
                    UserDisplayName = "System (auto)",
                    SubjectId = redacted,
                    SubjectName = redacted,
                    Action =
                        $"Suppressed all email due to {reasonText} (found on the Postmark {stream} stream suppression dump)."
                        + $" Evidence count {suppression.ConsecutiveFailureCount}."
                        + " Opt-in preferences were not changed.",
                }
            );
        }
    }
}
