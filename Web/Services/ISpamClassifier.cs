#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Web.Models;

namespace Web.Services
{
    /// <summary>
    /// The result of classifying a held (non-directory) committee email. A missing or failed
    /// classification is represented by an <see cref="SpamVerdict.Unknown"/> verdict - never an exception -
    /// so a classifier problem can never cause a legitimate message to be dropped or auto-rejected.
    /// </summary>
    public sealed class SpamClassificationResult
    {
        public SpamVerdict Verdict { get; init; } = SpamVerdict.Unknown;

        public SpamConfidence Confidence { get; init; } = SpamConfidence.Unknown;

        public string? Reason { get; init; }
    }

    /// <summary>
    /// Classifies a held committee email as spam or not. Implementations must be safe to call from the
    /// mail poller's background loop and must not throw for classification failures (return an Unknown
    /// result instead); only cancellation may propagate.
    /// </summary>
    public interface ISpamClassifier
    {
        /// <summary>
        /// True when a real, configured classifier is backing this instance. False for the no-op used when
        /// no API key is configured - lets the poller warn if classification is enabled but unusable.
        /// </summary>
        bool IsAvailable { get; }

        Task<SpamClassificationResult> ClassifyAsync(
            string? senderEmail,
            string? senderName,
            string? subject,
            string? body,
            CancellationToken ct
        );
    }

    /// <summary>
    /// No-op classifier registered when no Anthropic API key is configured. Always returns an Unknown
    /// verdict, so the poller falls back to notifying moderators for every held message.
    /// </summary>
    public sealed class DisabledSpamClassifier : ISpamClassifier
    {
        public bool IsAvailable => false;

        public Task<SpamClassificationResult> ClassifyAsync(
            string? senderEmail,
            string? senderName,
            string? subject,
            string? body,
            CancellationToken ct
        )
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new SpamClassificationResult());
        }
    }
}
