#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Web.Configuration;

namespace Web.Services
{
    /// <summary>
    /// What one provider-side reactivation attempt did, per stream. The caller turns a failed
    /// attempt into a visible warning: "cleared here, still suppressed at the provider" must
    /// never read as a silent 200.
    /// </summary>
    public sealed class PostmarkReactivationResult
    {
        /// <summary>
        /// The outcome when no provider integration is configured to act against (see
        /// <see cref="NotConfiguredPostmarkReactivationService"/>): nothing was attempted, and
        /// nothing needs a warning - without a server token the send path does not go through
        /// Postmark's suppression filter, so there is no provider-side entry dropping mail.
        /// </summary>
        public static readonly PostmarkReactivationResult NotConfigured =
            new(0, Array.Empty<string>(), skippedNotConfigured: true);

        public PostmarkReactivationResult(int streamsAttempted, IReadOnlyList<string> failedStreams)
            : this(streamsAttempted, failedStreams, skippedNotConfigured: false) { }

        private PostmarkReactivationResult(
            int streamsAttempted,
            IReadOnlyList<string> failedStreams,
            bool skippedNotConfigured
        )
        {
            StreamsAttempted = streamsAttempted;
            FailedStreams = failedStreams;
            SkippedNotConfigured = skippedNotConfigured;
        }

        public int StreamsAttempted { get; }

        /// <summary>The streams whose delete call failed; empty on full success.</summary>
        public IReadOnlyList<string> FailedStreams { get; }

        /// <summary>
        /// True when the attempt was skipped because no provider integration is configured -
        /// neither a success (nothing was reactivated) nor a warnable failure (there is no
        /// provider-side suppression affecting the send path). Callers branch on this before
        /// <see cref="Succeeded"/>.
        /// </summary>
        public bool SkippedNotConfigured { get; }

        /// <summary>
        /// True only when every configured stream confirmed the delete. Zero attempted streams
        /// (no streams configured) counts as failure: the provider side was not touched, which
        /// is exactly what the warning exists to say.
        /// </summary>
        public bool Succeeded => StreamsAttempted > 0 && FailedStreams.Count == 0;
    }

    /// <summary>
    /// Reactivates an address on Postmark's stream suppression lists when an admin clears a
    /// <see cref="Models.SuppressionReason.ProviderUnsubscribe"/> record (issue #11), so the
    /// two-system clear is one action. Every configured stream is targeted, because the COHAD
    /// record does not store which stream suppressed the address (only the diagnostic text) -
    /// and deleting an entry that does not exist is a provider-side no-op, so over-targeting
    /// costs nothing.
    /// </summary>
    public interface IPostmarkReactivationService
    {
        /// <summary>
        /// Attempts the delete on every configured stream, never throwing for a failed
        /// provider call - including a provider timeout: one stream's failure must not stop
        /// the other's attempt, and the caller's COHAD clear has already happened, so the
        /// result is how the caller learns what to warn about. Only the caller's own
        /// cancellation propagates.
        /// </summary>
        Task<PostmarkReactivationResult> ReactivateAsync(string email, CancellationToken cancellationToken);
    }

    public sealed class PostmarkReactivationService : IPostmarkReactivationService
    {
        private readonly IPostmarkSuppressionClient _client;
        private readonly PostmarkOptions _options;
        private readonly ILogger<PostmarkReactivationService> _logger;

        public PostmarkReactivationService(
            IPostmarkSuppressionClient client,
            IOptions<PostmarkOptions> options,
            ILogger<PostmarkReactivationService> logger
        )
        {
            _client = client;
            _options = options.Value;
            _logger = logger;
        }

        public async Task<PostmarkReactivationResult> ReactivateAsync(
            string email,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The per-stream deletes are independent calls against a thread-safe HttpClient, so
            // they run concurrently: with a hung provider, the caller waits out one timeout, not
            // one per stream.
            var streams = _options.GetConfiguredStreams();
            var attempts = await Task.WhenAll(
                streams.Select(stream => AttemptAsync(stream, email, cancellationToken))
            );

            return new PostmarkReactivationResult(
                streams.Count,
                attempts.Where(a => !a.Succeeded).Select(a => a.Stream).ToList()
            );
        }

        private async Task<(string Stream, bool Succeeded)> AttemptAsync(
            string stream,
            string email,
            CancellationToken cancellationToken
        )
        {
            try
            {
                await _client.ReactivateAsync(stream, email, cancellationToken);
                return (stream, true);
            }
            catch (Exception ex)
                when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // Cancellation-shaped exceptions are only rethrown when the CALLER cancelled:
                // HttpClient reports its own timeout as TaskCanceledException, and a hung
                // provider is precisely the failure this result exists to report, not a reason
                // to abandon the other stream and escape as an unhandled 500.
                _logger.LogError(
                    ex,
                    "Reactivating {Email} on the Postmark {Stream} stream failed.",
                    email,
                    stream
                );
                return (stream, false);
            }
        }
    }

    /// <summary>
    /// The registration when no <c>Postmark:ServerToken</c> is configured (webhook-only
    /// Postmark, or no Postmark at all) - the <c>DisabledSpamClassifier</c> precedent. In those
    /// deployments sends do not pass through Postmark's suppression filter, so there is no
    /// provider-side entry to reactivate and warning the admin about a "failed" provider call
    /// on every clear would report a false, unresolvable problem. MockData does not use this:
    /// its suppression client is the in-memory fake, which needs no token.
    /// </summary>
    public sealed class NotConfiguredPostmarkReactivationService : IPostmarkReactivationService
    {
        public Task<PostmarkReactivationResult> ReactivateAsync(
            string email,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(PostmarkReactivationResult.NotConfigured);
        }
    }
}
