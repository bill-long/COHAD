#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Web.Configuration;

namespace Web.Services
{
    /// <summary>
    /// What one provider-side reactivation attempt did, per stream. The caller turns anything
    /// short of full success into a visible warning: "cleared here, still suppressed at the
    /// provider" must never read as a silent 200.
    /// </summary>
    public sealed class PostmarkReactivationResult
    {
        public PostmarkReactivationResult(int streamsAttempted, IReadOnlyList<string> failedStreams)
        {
            StreamsAttempted = streamsAttempted;
            FailedStreams = failedStreams;
        }

        public int StreamsAttempted { get; }

        /// <summary>The streams whose delete call failed; empty on full success.</summary>
        public IReadOnlyList<string> FailedStreams { get; }

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
        /// provider call: one stream's failure must not stop the other's attempt, and the
        /// caller's COHAD clear has already happened - the result is how the caller learns
        /// what to warn about. Cancellation still propagates.
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
            var streams = _options.GetConfiguredStreams();
            var failed = new List<string>();

            foreach (var stream in streams)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await _client.ReactivateAsync(stream, email, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The client already logged the provider's answer; this line ties the
                    // failure to the reactivation attempt as a whole.
                    failed.Add(stream);
                    _logger.LogError(
                        ex,
                        "Reactivating {Email} on the Postmark {Stream} stream failed.",
                        email,
                        stream
                    );
                }
            }

            return new PostmarkReactivationResult(streams.Count, failed);
        }
    }
}
