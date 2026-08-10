#nullable enable
using System;
using System.Threading.Tasks;
using Web.Models;
using Web.Services.Repositories;

namespace Web.Services
{
    /// <summary>
    /// What a <see cref="IEmailSuppressionService.RecordAsync"/> call did: the stored record, and
    /// whether this call changed it. <see cref="Applied"/> is false when the evidence was a replay
    /// (same <c>evidenceKey</c> as the record already holds) - callers that audit must gate on it,
    /// or a webhook retry writes a second "Suppressed due to hard bounce" entry for one bounce.
    /// </summary>
    public sealed class SuppressionRecordOutcome
    {
        public SuppressionRecordOutcome(EmailSuppression suppression, bool applied)
        {
            Suppression = suppression;
            Applied = applied;
        }

        public EmailSuppression Suppression { get; }

        /// <summary>Whether this call wrote anything.</summary>
        public bool Applied { get; }
    }

    /// <summary>
    /// What a clear call did. <see cref="Cleared"/> is true only when THIS call performed the
    /// transition - an idempotent no-op on an already-cleared record returns false, so an audit
    /// entry can never attribute the clear to someone who took no action. <see cref="Suppression"/>
    /// is null when no record exists at all.
    /// </summary>
    public sealed class SuppressionClearOutcome
    {
        public SuppressionClearOutcome(EmailSuppression? suppression, bool cleared)
        {
            Suppression = suppression;
            Cleared = cleared;
        }

        public EmailSuppression? Suppression { get; }

        /// <summary>Whether this call performed the clear.</summary>
        public bool Cleared { get; }
    }

    /// <summary>
    /// The single place that mutates suppression records. Both writers - the RFC 8058 one-click
    /// endpoint and the delivery-event path - and the admin actions converge here, so the record
    /// lifecycle (what repeat evidence updates, what a re-suppression resets, what a clear stamps)
    /// is defined once. See docs/email-suppression-and-unsubscribe.md, Part 3.
    /// </summary>
    public interface IEmailSuppressionService
    {
        /// <summary>
        /// Records evidence that mail to the address should stop, creating the suppression or
        /// updating the existing record per the lifecycle rules.
        /// <para>
        /// <paramref name="evidenceKey"/> makes a retried delivery makes the write idempotent per
        /// piece of evidence: when the active record already carries the same key, nothing is
        /// written and <see cref="SuppressionRecordOutcome.Applied"/> is false. The delivery-event
        /// path passes the event's deduplicated id, because marking the event processed happens
        /// AFTER this call and can fail - without the key, that at-least-once replay inflates
        /// <c>ConsecutiveFailureCount</c> into a lie about how often the address failed.
        /// </para>
        /// <para>
        /// <paramref name="eventUtc"/> is the provider's own timestamp for the event, when the
        /// evidence carries one (the suppression dump's <c>CreatedAt</c>). It becomes
        /// <c>SuppressedUtc</c> on a new episode: for a reconciled record, "when the suppression
        /// began" is the provider's date - the address stopped receiving mail then, not when the
        /// reconciliation happened to notice. First/last-seen keep their meaning (when the
        /// evidence arrived HERE). A missing or future-dated value falls back to now.
        /// </para>
        /// <para>
        /// Throws <see cref="ArgumentException"/> for a blank address, and
        /// <see cref="ConcurrencyConflictException"/> when every retry of the write loses its
        /// race - callers on request paths surface that as 409, not 500.
        /// </para>
        /// </summary>
        Task<SuppressionRecordOutcome> RecordAsync(
            string email,
            SuppressionReason reason,
            string suppressedBy,
            Guid? causingJobId,
            string? providerDiagnostic,
            string? evidenceKey = null,
            DateTime? eventUtc = null
        );

        /// <summary>
        /// Clears the suppression for an address so mail can flow again. Idempotent: an
        /// already-cleared record or a missing one is returned with
        /// <see cref="SuppressionClearOutcome.Cleared"/> false.
        /// <para>
        /// <paramref name="onlyIfReason"/> enforces "only this reason may be lifted" at the point
        /// of the write, not on a stale pre-read: if the record active AT WRITE TIME carries a
        /// different reason, nothing is cleared and the still-active record is returned. This is
        /// what keeps the resident resume from lifting a bounce suppression that arrived between
        /// the page's read and the click.
        /// </para>
        /// </summary>
        Task<SuppressionClearOutcome> ClearAsync(string email, string clearedBy, SuppressionReason? onlyIfReason = null);

        /// <summary>
        /// Clears the record with this document id. Exists for the admin surface, which acts on a
        /// listed row: a hand-authored document whose id does not match
        /// <see cref="EmailSuppression.MakeId"/> of its own Email is unreachable by address and
        /// must still be clearable by the human looking at it.
        /// </summary>
        Task<SuppressionClearOutcome> ClearByIdAsync(string id, string clearedBy);
    }

    public class EmailSuppressionService : IEmailSuppressionService
    {
        /// <summary>
        /// Attempts before giving up on a lost race. Two writers racing on one address (a bounce
        /// webhook against a one-click, say) converge on the same document, so one retry nearly
        /// always suffices; the bound exists so a pathological interleaving surfaces as a failure
        /// rather than a loop.
        /// </summary>
        private const int MaxAttempts = 3;

        private readonly IEmailSuppressionRepository _repository;
        private readonly TimeProvider _timeProvider;

        public EmailSuppressionService(IEmailSuppressionRepository repository, TimeProvider timeProvider)
        {
            _repository = repository;
            _timeProvider = timeProvider;
        }

        public async Task<SuppressionRecordOutcome> RecordAsync(
            string email,
            SuppressionReason reason,
            string suppressedBy,
            Guid? causingJobId,
            string? providerDiagnostic,
            string? evidenceKey = null,
            DateTime? eventUtc = null
        )
        {
            // A blank address can never be mailed, so a suppression for one is not a safety
            // measure - it is a corrupt row whose only future is confusing the admin list.
            if (string.IsNullOrWhiteSpace(email))
                throw new ArgumentException("Email must not be empty.", nameof(email));
            if (string.IsNullOrWhiteSpace(suppressedBy))
                throw new ArgumentException("SuppressedBy must not be empty.", nameof(suppressedBy));

            ConcurrencyConflictException? lastConflict = null;

            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                var now = _timeProvider.GetUtcNow().UtcDateTime;
                // The provider's "when it began" is only ever in the past; a future-dated value
                // (provider clock skew) reads as now rather than ordering the record oddly.
                // ToUniversalTime makes non-Utc kinds deterministic (identity for Utc) and the
                // clamp compares the converted value so a non-Utc kind can't slip a future
                // timestamp past it.
                var eventAsUtc = eventUtc?.ToUniversalTime();
                var suppressedSince = eventAsUtc.HasValue && eventAsUtc.Value < now ? eventAsUtc.Value : now;
                var existing = await _repository.GetByEmailAsync(email);

                try
                {
                    if (existing == null)
                    {
                        var suppression = new EmailSuppression
                        {
                            Id = EmailSuppression.MakeId(email),
                            Email = EmailSuppression.NormalizeAddress(email),
                            Reason = reason,
                            ConsecutiveFailureCount = 1,
                            FirstSeenUtc = now,
                            LastSeenUtc = now,
                            CausingJobId = causingJobId,
                            SuppressedUtc = suppressedSince,
                            SuppressedBy = suppressedBy,
                            ProviderDiagnostic = providerDiagnostic,
                            LastEvidenceKey = evidenceKey,
                        };
                        await _repository.AddAsync(suppression);
                        return new SuppressionRecordOutcome(suppression, applied: true);
                    }

                    // The same piece of evidence, replayed. The delivery-event path is
                    // at-least-once (the event is marked processed only after this call), so
                    // counting a replay would turn one bounce into "Evidence count 3" on the very
                    // page an admin reads to decide typo-vs-dead-mailbox.
                    if (
                        existing.IsActive
                        && evidenceKey != null
                        && string.Equals(existing.LastEvidenceKey, evidenceKey, StringComparison.Ordinal)
                    )
                    {
                        return new SuppressionRecordOutcome(existing, applied: false);
                    }

                    existing.ConsecutiveFailureCount++;
                    existing.LastSeenUtc = now;
                    existing.LastEvidenceKey = evidenceKey;

                    if (existing.IsActive)
                    {
                        // Repeat evidence on an in-force suppression: the counters advance, and
                        // the diagnostic refreshes when the new evidence carries one - later
                        // provider text is at least as current as what it replaces. The original
                        // Reason/SuppressedUtc/SuppressedBy stay: they answer "why is this address
                        // suppressed", and the answer is still the first event.
                        if (!string.IsNullOrWhiteSpace(providerDiagnostic))
                            existing.ProviderDiagnostic = providerDiagnostic;
                        if (causingJobId.HasValue)
                            existing.CausingJobId = causingJobId;
                    }
                    else
                    {
                        // Re-suppression after a clear: a new episode. FirstSeenUtc survives so
                        // the record still reads as a history, but the "why" fields describe the
                        // event that put the suppression back in force, and the cleared stamps
                        // reset because the clear they describe has been overtaken.
                        existing.Reason = reason;
                        existing.SuppressedUtc = suppressedSince;
                        existing.SuppressedBy = suppressedBy;
                        existing.ProviderDiagnostic = providerDiagnostic;
                        existing.CausingJobId = causingJobId;
                        existing.ClearedUtc = null;
                        existing.ClearedBy = null;
                    }

                    await _repository.UpdateAsync(existing);
                    return new SuppressionRecordOutcome(existing, applied: true);
                }
                catch (ConcurrencyConflictException ex)
                {
                    // A lost race either way: another writer created the record first (the Add
                    // path) or wrote it between our read and write (the Update path). Re-reading
                    // converges - the next attempt sees the winner's record and applies this
                    // evidence on top of it.
                    lastConflict = ex;
                }
            }

            // The same type the repository throws, not a wrapper: "the write kept losing races"
            // is still a concurrency conflict, and callers on request paths answer it with 409
            // ("please try again") rather than a 500 that pages someone.
            throw new ConcurrencyConflictException(
                $"Failed to record a suppression after {MaxAttempts} lost races.",
                lastConflict!
            );
        }

        public Task<SuppressionClearOutcome> ClearAsync(
            string email,
            string clearedBy,
            SuppressionReason? onlyIfReason = null
        )
        {
            return ClearCoreAsync(() => _repository.GetByEmailAsync(email), clearedBy, onlyIfReason);
        }

        public Task<SuppressionClearOutcome> ClearByIdAsync(string id, string clearedBy)
        {
            return ClearCoreAsync(() => _repository.GetByIdAsync(id), clearedBy, onlyIfReason: null);
        }

        private async Task<SuppressionClearOutcome> ClearCoreAsync(
            Func<Task<EmailSuppression?>> read,
            string clearedBy,
            SuppressionReason? onlyIfReason
        )
        {
            if (string.IsNullOrWhiteSpace(clearedBy))
                throw new ArgumentException("ClearedBy must not be empty.", nameof(clearedBy));

            ConcurrencyConflictException? lastConflict = null;

            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                var existing = await read();
                if (existing == null)
                    return new SuppressionClearOutcome(null, cleared: false);

                // Idempotent: "make sure this address can receive mail" is satisfied by an
                // already-cleared record, and the original clear's stamps are the truthful ones.
                if (!existing.IsActive)
                    return new SuppressionClearOutcome(existing, cleared: false);

                // Enforced here, on the record this write cycle actually holds, not on a caller's
                // earlier read - between that read and this write the record can be cleared and
                // re-suppressed for a reason the caller is not allowed to lift.
                if (onlyIfReason.HasValue && existing.Reason != onlyIfReason.Value)
                    return new SuppressionClearOutcome(existing, cleared: false);

                existing.ClearedUtc = _timeProvider.GetUtcNow().UtcDateTime;
                existing.ClearedBy = clearedBy;

                try
                {
                    await _repository.UpdateAsync(existing);
                    return new SuppressionClearOutcome(existing, cleared: true);
                }
                catch (ConcurrencyConflictException ex)
                {
                    lastConflict = ex;
                }
            }

            throw new ConcurrencyConflictException(
                $"Failed to clear a suppression after {MaxAttempts} lost races.",
                lastConflict!
            );
        }
    }
}
