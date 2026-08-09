#nullable enable
using System;
using System.Threading.Tasks;
using Web.Models;
using Web.Services.Repositories;

namespace Web.Services
{
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
        /// updating the existing record per the lifecycle rules. Returns the stored record.
        /// Throws <see cref="ArgumentException"/> for a blank address.
        /// </summary>
        Task<EmailSuppression> RecordAsync(
            string email,
            SuppressionReason reason,
            string suppressedBy,
            Guid? causingJobId,
            string? providerDiagnostic
        );

        /// <summary>
        /// Clears the suppression so mail can flow again. Idempotent: clearing an already-cleared
        /// record changes nothing, and clearing an address with no record returns null. Returns
        /// the stored record, or null when none exists.
        /// </summary>
        Task<EmailSuppression?> ClearAsync(string email, string clearedBy);
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

        public async Task<EmailSuppression> RecordAsync(
            string email,
            SuppressionReason reason,
            string suppressedBy,
            Guid? causingJobId,
            string? providerDiagnostic
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
                            SuppressedUtc = now,
                            SuppressedBy = suppressedBy,
                            ProviderDiagnostic = providerDiagnostic,
                        };
                        await _repository.AddAsync(suppression);
                        return suppression;
                    }

                    existing.ConsecutiveFailureCount++;
                    existing.LastSeenUtc = now;

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
                        existing.SuppressedUtc = now;
                        existing.SuppressedBy = suppressedBy;
                        existing.ProviderDiagnostic = providerDiagnostic;
                        existing.CausingJobId = causingJobId;
                        existing.ClearedUtc = null;
                        existing.ClearedBy = null;
                    }

                    await _repository.UpdateAsync(existing);
                    return existing;
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

            throw new InvalidOperationException(
                $"Failed to record a suppression after {MaxAttempts} lost races.",
                lastConflict
            );
        }

        public async Task<EmailSuppression?> ClearAsync(string email, string clearedBy)
        {
            if (string.IsNullOrWhiteSpace(clearedBy))
                throw new ArgumentException("ClearedBy must not be empty.", nameof(clearedBy));

            ConcurrencyConflictException? lastConflict = null;

            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                var existing = await _repository.GetByEmailAsync(email);
                if (existing == null)
                    return null;

                // Idempotent: "make sure this address can receive mail" is satisfied by an
                // already-cleared record, and the original clear's stamps are the truthful ones.
                if (!existing.IsActive)
                    return existing;

                existing.ClearedUtc = _timeProvider.GetUtcNow().UtcDateTime;
                existing.ClearedBy = clearedBy;

                try
                {
                    await _repository.UpdateAsync(existing);
                    return existing;
                }
                catch (ConcurrencyConflictException ex)
                {
                    lastConflict = ex;
                }
            }

            throw new InvalidOperationException(
                $"Failed to clear a suppression after {MaxAttempts} lost races.",
                lastConflict
            );
        }
    }
}
