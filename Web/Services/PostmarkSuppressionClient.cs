#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
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
    /// Talks to Postmark's per-stream suppression API: reads the current suppression list
    /// (<c>GET /message-streams/{stream}/suppressions/dump</c>) for the periodic reconciliation
    /// (<see cref="PostmarkSuppressionSyncRunner"/>), and deletes a suppression entry
    /// (<c>POST /message-streams/{stream}/suppressions/delete</c>) so an admin clear of a
    /// <see cref="Models.SuppressionReason.ProviderUnsubscribe"/> record reactivates the address
    /// at the provider too (issue #11). See docs/email-suppression-and-unsubscribe.md, addendum.
    /// </summary>
    public interface IPostmarkSuppressionClient
    {
        Task<IReadOnlyList<PostmarkSuppressionDumpEntry>> GetSuppressionsAsync(
            string messageStream,
            CancellationToken cancellationToken
        );

        /// <summary>
        /// Deletes the address's suppression entry on the stream, so Postmark resumes delivering
        /// to it. Idempotent at the provider: deleting an entry that does not exist reports
        /// success ("Deleted"), so callers can target every stream without checking first.
        /// Throws when the entry could not be deleted - including when Postmark answers 200 but
        /// reports the entry "Failed" (e.g. a SpamComplaint suppression, which Postmark refuses
        /// to delete).
        /// </summary>
        Task ReactivateAsync(string messageStream, string emailAddress, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Minimal HTTP client for the suppression API - the first Postmark HTTP (non-SMTP) client
    /// in the codebase, deliberately scoped to the two endpoints the reconciler and the admin
    /// clear need. Auth is the same server token the SMTP transport uses
    /// (<see cref="PostmarkOptions.ServerToken"/>).
    /// </summary>
    public sealed class PostmarkSuppressionClient : IPostmarkSuppressionClient
    {
        public const string ApiBaseUrl = "https://api.postmarkapp.com";

        private readonly HttpClient _http;
        private readonly PostmarkOptions _options;
        private readonly ILogger<PostmarkSuppressionClient> _logger;

        public PostmarkSuppressionClient(
            HttpClient http,
            IOptions<PostmarkOptions> options,
            ILogger<PostmarkSuppressionClient> logger
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
            RequireMessageStream(messageStream);
            RequireServerToken();

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

        public async Task ReactivateAsync(
            string messageStream,
            string emailAddress,
            CancellationToken cancellationToken
        )
        {
            RequireMessageStream(messageStream);
            if (string.IsNullOrWhiteSpace(emailAddress))
                throw new ArgumentException("Email address must not be empty.", nameof(emailAddress));
            RequireServerToken();

            var url =
                $"{ApiBaseUrl}/message-streams/{Uri.EscapeDataString(messageStream)}/suppressions/delete";
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("X-Postmark-Server-Token", _options.ServerToken);
            request.Headers.Add("Accept", "application/json");
            request.Content = new StringContent(
                JsonSerializer.Serialize(
                    new { Suppressions = new[] { new { EmailAddress = emailAddress } } }
                ),
                Encoding.UTF8,
                "application/json"
            );

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "Postmark suppression delete on stream {MessageStream} failed: {Status} {Body}",
                    messageStream,
                    (int)response.StatusCode,
                    body
                );
                response.EnsureSuccessStatusCode();
            }

            // Postmark answers 200 with a per-entry Status: "Deleted" on success (including the
            // no-op delete of an entry that does not exist), "Failed" with a Message otherwise
            // (e.g. a SpamComplaint entry, which only the recipient can lift). Strict in the
            // OPPOSITE direction from the dump parsing: an ambiguous answer here must read as
            // failure, because a false "reactivated" leaves the admin believing mail flows while
            // Postmark still silently drops it - a false failure only shows a spurious warning.
            var failure = ExtractReactivationFailure(body, emailAddress);
            if (failure != null)
            {
                _logger.LogError(
                    "Postmark did not delete the suppression for {Email} on stream {MessageStream}: {Detail}",
                    emailAddress,
                    messageStream,
                    failure
                );
                throw new InvalidOperationException(
                    $"Postmark did not delete the suppression on the {messageStream} stream: {failure}"
                );
            }
        }

        /// <summary>
        /// Returns null when the response confirms the entry was deleted, otherwise a
        /// human-readable description of why not (Postmark's own Message when it sent one).
        /// </summary>
        internal static string? ExtractReactivationFailure(string body, string emailAddress)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (
                    doc.RootElement.ValueKind != JsonValueKind.Object
                    || !doc.RootElement.TryGetProperty("Suppressions", out var suppressions)
                    || suppressions.ValueKind != JsonValueKind.Array
                )
                {
                    return "the response carried no Suppressions array.";
                }

                foreach (var item in suppressions.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        continue;
                    var entryAddress = GetOptionalString(item, "EmailAddress");
                    if (
                        entryAddress == null
                        || !string.Equals(entryAddress.Trim(), emailAddress.Trim(), StringComparison.OrdinalIgnoreCase)
                    )
                    {
                        continue;
                    }

                    var status = GetOptionalString(item, "Status");
                    if (string.Equals(status, "Deleted", StringComparison.Ordinal))
                        return null;

                    var message = GetOptionalString(item, "Message");
                    return message ?? $"status {status ?? "(absent)"}.";
                }

                return "the response carried no entry for the address.";
            }
            catch (JsonException)
            {
                return "the response was not parseable JSON.";
            }
        }

        private static void RequireMessageStream(string messageStream)
        {
            if (string.IsNullOrWhiteSpace(messageStream))
                throw new ArgumentException("Message stream must not be empty.", nameof(messageStream));
        }

        private void RequireServerToken()
        {
            if (string.IsNullOrWhiteSpace(_options.ServerToken))
                throw new InvalidOperationException(
                    "Postmark:ServerToken must be configured to use the suppression API."
                );
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
