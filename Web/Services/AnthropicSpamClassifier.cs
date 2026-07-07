#nullable enable
using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Core;
using Anthropic.Helpers;
using Anthropic.Models.Messages;
using Anthropic.Services;
using Microsoft.Extensions.Logging;
using Web.Models;

namespace Web.Services
{
    /// <summary>
    /// Classifies held committee emails using the Anthropic API. Only non-directory senders reach this
    /// path, so the classifier is deciding whether an unsolicited message is obvious spam. It never throws
    /// for a classification failure — any error yields an Unknown verdict so the message continues on the
    /// normal moderator-notification path (fail-safe: a classifier outage must not drop or reject email).
    /// </summary>
    public sealed class AnthropicSpamClassifier : ISpamClassifier
    {
        // The body is only used for spam signal; cap it to bound token cost — spam intent is evident early.
        private const int MaxBodyChars = 6000;

        // Bound raw input before regex work so a pathologically large body can't dominate a poll cycle.
        private const int MaxRawBodyChars = 50_000;

        // Hard per-call ceiling. The classifier runs inline in the sequential poll loop, so a slow or hung
        // Anthropic endpoint (SDK default timeout is minutes, with retries) would otherwise stall the whole
        // committee poll cycle. On timeout we fail safe to an Unknown verdict.
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

        private const string SystemPrompt =
            "You are a spam filter for a residential homeowners-association committee mailbox. You receive "
            + "emails from senders who are NOT residents in the directory. Decide whether a message is "
            + "unsolicited spam: cold sales or marketing outreach, mass promotional blasts, phishing, "
            + "financing/lead-generation pitches, SEO/web-design solicitations, or similar. Legitimate mail "
            + "from a neighbor, vendor, or community member writing about actual HOA/community business is "
            + "NOT spam, even if the sender is unknown. Only mark isSpam true when you are genuinely "
            + "confident it is unsolicited spam; when unsure, prefer NOT spam with lower confidence.";

        private readonly AnthropicClient _client;
        private readonly string _model;
        private readonly ILogger<AnthropicSpamClassifier> _logger;

        public AnthropicSpamClassifier(string apiKey, string model, ILogger<AnthropicSpamClassifier> logger)
        {
            _client = new AnthropicClient(new ClientOptions { ApiKey = apiKey });
            _model = model;
            _logger = logger;
        }

        public bool IsAvailable => true;

        public async Task<SpamClassificationResult> ClassifyAsync(
            string? senderEmail,
            string? senderName,
            string? subject,
            string? body,
            CancellationToken ct
        )
        {
            // Bound the call so a hung endpoint can't stall the poll loop. The linked source cancels on the
            // caller's ct (real shutdown) or after RequestTimeout, whichever comes first.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(RequestTimeout);

            try
            {
                var response = await _client.Messages.Create<SpamAssessment>(
                    new MessageCreateParams
                    {
                        Model = _model,
                        MaxTokens = 512,
                        System = SystemPrompt,
                        Messages =
                        [
                            new() { Role = Role.User, Content = BuildPrompt(senderEmail, senderName, subject, body) },
                        ],
                    },
                    timeoutCts.Token
                );

                var assessment = response.Content.Count > 0 ? response.Content[0].Parsed() : null;
                return MapAssessment(assessment);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Our per-call deadline fired (not a real shutdown, whose ct would be cancelled) — fail safe.
                _logger.LogWarning(
                    "Spam classification timed out after {Timeout}s; treating as Unknown",
                    RequestTimeout.TotalSeconds
                );
                return new SpamClassificationResult { Reason = "Classification timeout" };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Fail-safe: never let a classifier error reject or drop an email. An Unknown verdict routes
                // the message through the normal moderator-notification path.
                _logger.LogWarning(ex, "Spam classification failed; treating as Unknown");
                return new SpamClassificationResult { Reason = "Classification error" };
            }
        }

        /// <summary>Maps the model's raw structured assessment onto the internal result. Pure — unit-tested.</summary>
        internal static SpamClassificationResult MapAssessment(SpamAssessment? assessment)
        {
            if (assessment == null)
                return new SpamClassificationResult { Reason = "No classifier output" };

            return new SpamClassificationResult
            {
                Verdict = assessment.IsSpam ? SpamVerdict.Spam : SpamVerdict.NotSpam,
                Confidence = ParseConfidence(assessment.Confidence),
                Reason = string.IsNullOrWhiteSpace(assessment.Reason) ? null : assessment.Reason.Trim(),
            };
        }

        internal static SpamConfidence ParseConfidence(string? confidence) =>
            confidence?.Trim().ToLowerInvariant() switch
            {
                "high" => SpamConfidence.High,
                "medium" => SpamConfidence.Medium,
                "low" => SpamConfidence.Low,
                _ => SpamConfidence.Unknown,
            };

        private static string BuildPrompt(string? senderEmail, string? senderName, string? subject, string? body)
        {
            // Reduce to visible text BEFORE truncating, so the budget isn't spent on markup — otherwise a
            // bulk-HTML solicitation (exactly what this feature targets) can be cut before its sales copy.
            var text = ToPlainText(body ?? string.Empty);
            if (text.Length > MaxBodyChars)
                text = text.Substring(0, MaxBodyChars) + " [...truncated...]";

            return new StringBuilder()
                .Append("Classify this email.\n\n")
                .Append("From name: ").Append(senderName ?? "(none)").Append('\n')
                .Append("From address: ").Append(senderEmail ?? "(none)").Append('\n')
                .Append("Subject: ").Append(subject ?? "(none)").Append('\n')
                .Append("Body:\n")
                .Append(text)
                .ToString();
        }

        /// <summary>
        /// Cheap HTML-to-text: drops script/style blocks, strips tags, decodes entities, and collapses
        /// whitespace. Harmless on plain-text bodies (no tags to remove). Raw input is capped first so the
        /// regex work stays bounded. Internal for unit testing.
        /// </summary>
        internal static string ToPlainText(string body)
        {
            if (string.IsNullOrEmpty(body))
                return string.Empty;

            if (body.Length > MaxRawBodyChars)
                body = body.Substring(0, MaxRawBodyChars);

            var noScripts = Regex.Replace(
                body,
                "<(script|style)[^>]*>.*?</\\1>",
                " ",
                RegexOptions.IgnoreCase | RegexOptions.Singleline
            );
            var noTags = Regex.Replace(noScripts, "<[^>]+>", " ");
            var decoded = System.Net.WebUtility.HtmlDecode(noTags);
            return Regex.Replace(decoded, "\\s+", " ").Trim();
        }

        /// <summary>
        /// The structured shape the model is forced to return via the SDK's structured-output support.
        /// </summary>
        public sealed class SpamAssessment
        {
            [SchemaProperty("true only if this email is unsolicited spam, marketing, phishing, or a cold sales/lead-generation pitch")]
            public bool IsSpam { get; set; }

            [SchemaProperty("Your confidence in the verdict", Enum = new object[] { "high", "medium", "low" })]
            public string Confidence { get; set; } = "";

            [SchemaProperty("One short sentence explaining the verdict")]
            public string Reason { get; set; } = "";
        }
    }
}
