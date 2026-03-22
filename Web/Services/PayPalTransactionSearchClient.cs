using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;

namespace Web.Services
{
    /// <summary>
    /// OAuth + Transaction Search (<c>/v1/reporting/transactions</c>) for the merchant account.
    /// </summary>
    public sealed class PayPalTransactionSearchClient
    {
        private const int MaxPageSize = 500;
        private readonly HttpClient _http;
        private readonly PayPalOptions _options;
        private readonly ILogger<PayPalTransactionSearchClient> _logger;
        private readonly object _tokenLock = new();
        private string _accessToken;
        private DateTimeOffset _accessTokenExpiresUtc;

        public PayPalTransactionSearchClient(
            HttpClient http,
            IOptions<PayPalOptions> options,
            ILogger<PayPalTransactionSearchClient> logger)
        {
            _http = http;
            _options = options.Value;
            _logger = logger;
        }

        public async Task<IReadOnlyList<JObject>> ListAllTransactionDetailsAsync(
            DateTime startUtc,
            DateTime endUtcExclusive,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(_options.ClientId) || string.IsNullOrWhiteSpace(_options.ClientSecret))
            {
                throw new InvalidOperationException("PayPal ClientId and ClientSecret must be configured.");
            }

            var baseUrl = (_options.ApiBaseUrl ?? "https://api-m.paypal.com").TrimEnd('/');
            // PayPal query params are second-precision; align range and windows to whole UTC seconds so
            // adjacent windows neither overlap nor leave gaps when sub-second values are present.
            var rangeStart = FloorUtcToWholeSeconds(startUtc);
            var rangeEndExclusive = CeilingUtcToWholeSeconds(endUtcExclusive);
            if (rangeEndExclusive <= rangeStart)
            {
                return Array.Empty<JObject>();
            }

            var results = new List<JObject>();
            foreach (var (winStart, winEnd) in PayPalDateRangeChunker.EnumerateWindowsUtc(rangeStart, rangeEndExclusive))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var ws = FloorUtcToWholeSeconds(winStart);
                var we = FloorUtcToWholeSeconds(winEnd);
                if (we <= ws)
                {
                    continue;
                }

                // winEnd is exclusive; last instant in the window is one second before that boundary.
                var inclusiveEnd = we.AddSeconds(-1);
                if (inclusiveEnd < ws)
                {
                    continue;
                }

                var startParam = FormatPayPalDate(ws);
                var endParam = FormatPayPalDate(inclusiveEnd);

                var page = 1;
                var totalPages = 1;
                while (page <= totalPages)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var url =
                        $"{baseUrl}/v1/reporting/transactions?{BuildQuery(startParam, endParam, page)}";
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Authorization = new AuthenticationHeaderValue(
                        "Bearer",
                        await GetAccessTokenAsync(baseUrl, cancellationToken).ConfigureAwait(false));

                    using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                    var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogError(
                            "PayPal Transaction Search failed: {Status} {Body}",
                            (int)response.StatusCode,
                            body);
                        response.EnsureSuccessStatusCode();
                    }

                    var json = JObject.Parse(body);
                    totalPages = Math.Max(1, json.Value<int?>("total_pages") ?? 1);
                    var details = json["transaction_details"] as JArray;
                    if (details != null)
                    {
                        foreach (var d in details)
                        {
                            if (d is JObject o)
                            {
                                results.Add(o);
                            }
                        }
                    }

                    page++;
                }
            }

            return results;
        }

        private static string BuildQuery(string startParam, string endParam, int page)
        {
            return "start_date=" + Uri.EscapeDataString(startParam)
                + "&end_date=" + Uri.EscapeDataString(endParam)
                + "&fields=all"
                + "&page_size=" + MaxPageSize
                + "&page=" + page;
        }

        private static string FormatPayPalDate(DateTime utc)
        {
            if (utc.Kind != DateTimeKind.Utc)
            {
                utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            }

            return utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }

        private static DateTime FloorUtcToWholeSeconds(DateTime utc)
        {
            if (utc.Kind != DateTimeKind.Utc)
            {
                utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            }

            var ticks = utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond;
            return new DateTime(ticks, DateTimeKind.Utc);
        }

        /// <summary>
        /// Smallest whole-second UTC instant strictly after <paramref name="utc"/> when it has a fractional second;
        /// otherwise returns <paramref name="utc"/> floored to seconds (for exclusive-range upper bounds).
        /// </summary>
        private static DateTime CeilingUtcToWholeSeconds(DateTime utc)
        {
            var floor = FloorUtcToWholeSeconds(utc);
            return utc > floor ? floor.AddSeconds(1) : floor;
        }

        private async Task<string> GetAccessTokenAsync(string baseUrl, CancellationToken cancellationToken)
        {
            lock (_tokenLock)
            {
                if (!string.IsNullOrEmpty(_accessToken) &&
                    DateTimeOffset.UtcNow < _accessTokenExpiresUtc)
                {
                    return _accessToken;
                }
            }

            var tokenUrl = $"{baseUrl}/v1/oauth2/token";
            using var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl);
            var raw = $"{_options.ClientId}:{_options.ClientSecret}";
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)));
            request.Content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "client_credentials")
            });

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("PayPal OAuth failed: {Status} {Body}", (int)response.StatusCode, body);
                response.EnsureSuccessStatusCode();
            }

            var json = JObject.Parse(body);
            var token = json.Value<string>("access_token");
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException("PayPal OAuth response did not include a usable access_token.");
            }

            var expiresIn = json.Value<int?>("expires_in") ?? 3600;
            lock (_tokenLock)
            {
                _accessToken = token;
                _accessTokenExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn - 120));
                return _accessToken;
            }
        }
    }
}
