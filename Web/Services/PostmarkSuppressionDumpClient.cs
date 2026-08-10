#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Web.Configuration;

namespace Web.Services
{
    /// <summary>
    /// One entry of a Postmark message stream's suppression dump. Strings are kept as delivered
    /// (including <see cref="CreatedAt"/>) - the reconciler records them verbatim as evidence
    /// rather than re-interpreting provider data.
    /// </summary>
    public sealed class PostmarkSuppressionDumpEntry
    {
        public string EmailAddress { get; set; } = string.Empty;

        /// <summary>Postmark's <c>SuppressionReason</c>: ManualSuppression, HardBounce, SpamComplaint, or a future value.</summary>
        public string? SuppressionReason { get; set; }

        /// <summary>Postmark's <c>Origin</c>: Recipient, Customer, or Administration.</summary>
        public string? Origin { get; set; }

        /// <summary>Postmark's <c>CreatedAt</c> timestamp, as delivered.</summary>
        public string? CreatedAt { get; set; }
    }

    /// <summary>
    /// Reads a Postmark message stream's current suppression list
    /// (<c>GET /message-streams/{stream}/suppressions/dump</c>). The only consumer is the
    /// periodic reconciliation (<see cref="PostmarkSuppressionSyncRunner"/>), which mirrors
    /// Postmark-layer suppressions COHAD never saw a webhook for into the COHAD suppression
    /// list. See docs/email-suppression-and-unsubscribe.md, addendum.
    /// </summary>
    public interface IPostmarkSuppressionDumpClient
    {
        Task<IReadOnlyList<PostmarkSuppressionDumpEntry>> GetSuppressionsAsync(
            string messageStream,
            CancellationToken cancellationToken
        );
    }

    /// <summary>
    /// Minimal HTTP client for the suppression dump - the first Postmark HTTP (non-SMTP) client
    /// in the codebase, deliberately scoped to the one endpoint the reconciler needs. Auth is the
    /// same server token the SMTP transport uses (<see cref="PostmarkOptions.ServerToken"/>).
    /// </summary>
    public sealed class PostmarkSuppressionDumpClient : IPostmarkSuppressionDumpClient
    {
        public const string ApiBaseUrl = "https://api.postmarkapp.com";

        private readonly HttpClient _http;
        private readonly PostmarkOptions _options;
        private readonly ILogger<PostmarkSuppressionDumpClient> _logger;

        public PostmarkSuppressionDumpClient(
            HttpClient http,
            IOptions<PostmarkOptions> options,
            ILogger<PostmarkSuppressionDumpClient> logger
        )
        {
            _http = http;
            _options = options.Value;
            _logger = logger;
        }

        public async Task<IReadOnlyList<PostmarkSuppressionDumpEntry>> GetSuppressionsAsync(
            string messageStream,
            CancellationToken cancellationToken
        )
        {
            if (string.IsNullOrWhiteSpace(messageStream))
                throw new ArgumentException("Message stream must not be empty.", nameof(messageStream));
            if (string.IsNullOrWhiteSpace(_options.ServerToken))
                throw new InvalidOperationException(
                    "Postmark:ServerToken must be configured to read the suppression dump."
                );

            var url =
                $"{ApiBaseUrl}/message-streams/{Uri.EscapeDataString(messageStream)}/suppressions/dump";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Postmark-Server-Token", _options.ServerToken);
            request.Headers.Add("Accept", "application/json");

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "Postmark suppression dump for stream {MessageStream} failed: {Status} {Body}",
                    messageStream,
                    (int)response.StatusCode,
                    body
                );
                response.EnsureSuccessStatusCode();
            }

            return ParseDump(body, messageStream);
        }

        /// <summary>
        /// Parses the dump body. Lenient in the same direction as the webhook handlers: Postmark's
        /// schema says these fields are strings, but a drifted or unexpected value reads as absent
        /// rather than throwing the whole reconciliation into a failure loop. An entry with no
        /// usable address is skipped with a warning - it can never be matched against the COHAD
        /// suppression list, and skipping it must not lose the rest of the dump.
        /// </summary>
        internal List<PostmarkSuppressionDumpEntry> ParseDump(string body, string messageStream)
        {
            var entries = new List<PostmarkSuppressionDumpEntry>();
            using var doc = JsonDocument.Parse(body);

            if (
                doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("Suppressions", out var suppressions)
                || suppressions.ValueKind != JsonValueKind.Array
            )
            {
                _logger.LogWarning(
                    "Postmark suppression dump for stream {MessageStream} carried no Suppressions array.",
                    messageStream
                );
                return entries;
            }

            foreach (var item in suppressions.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var email = GetOptionalString(item, "EmailAddress");
                if (email == null)
                {
                    _logger.LogWarning(
                        "Postmark suppression dump for stream {MessageStream} carried an entry with no EmailAddress - skipping it.",
                        messageStream
                    );
                    continue;
                }

                entries.Add(
                    new PostmarkSuppressionDumpEntry
                    {
                        EmailAddress = email,
                        SuppressionReason = GetOptionalString(item, "SuppressionReason"),
                        Origin = GetOptionalString(item, "Origin"),
                        CreatedAt = GetOptionalString(item, "CreatedAt"),
                    }
                );
            }

            return entries;
        }

        private static string? GetOptionalString(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
                return null;
            var value = prop.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }
}
