using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using Web.Models;
using Web.Services.Repositories;

namespace Web.Services
{
    /// <summary>
    /// Imports PayPal Transaction Search rows. Associates each payment with a <see cref="Payment.HomeId"/> when the
    /// payer email matches a resident, home email, associated user, or a user account that owns a home.
    /// </summary>
    public sealed class PayPalPaymentSyncRunner
    {
        private readonly PayPalTransactionSearchClient _payPal;
        private readonly IPaymentRepository _payments;
        private readonly IUserRepository _users;
        private readonly IHomeRepository _homes;
        private readonly IResidentRepository _residents;
        private readonly IOptions<PayPalOptions> _options;
        private readonly ILogger<PayPalPaymentSyncRunner> _logger;

        public PayPalPaymentSyncRunner(
            PayPalTransactionSearchClient payPal,
            IPaymentRepository payments,
            IUserRepository users,
            IHomeRepository homes,
            IResidentRepository residents,
            IOptions<PayPalOptions> options,
            ILogger<PayPalPaymentSyncRunner> logger
        )
        {
            _payPal = payPal;
            _payments = payments;
            _users = users;
            _homes = homes;
            _residents = residents;
            _options = options;
            _logger = logger;
        }

        public async Task<PayPalPaymentSyncResult> RunAsync(CancellationToken cancellationToken = default)
        {
            var opt = _options.Value;
            var lookback = Math.Max(1, opt.SyncLookbackDays);
            var endUtc = DateTime.UtcNow;
            var startUtc = endUtc.AddDays(-lookback);

            _logger.LogInformation(
                "PayPal sync run: lookbackDays={Lookback}, utcRange=[{Start:o}, {End:o})",
                lookback,
                startUtc,
                endUtc
            );

            var users = await _users.GetAllAsync().ConfigureAwait(false);
            var homes = await _homes.GetAllAsync().ConfigureAwait(false);
            var allResidents = await _residents.GetAllAsync().ConfigureAwait(false);
            var residentsByHome = allResidents.GroupBy(r => r.HomeId).ToDictionary(g => g.Key, g => g.ToList());
            var emailToUniqueId = BuildEmailIndex(users);
            var homeLookup = new PayerEmailHomeLookup(homes, residentsByHome, users);

            var details = await _payPal
                .ListAllTransactionDetailsAsync(startUtc, endUtc, cancellationToken)
                .ConfigureAwait(false);

            var result = new PayPalPaymentSyncResult { TransactionsRead = details.Count };

            var eligible = new List<EligiblePayPalRow>();
            foreach (var detail in details)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var txInfo = detail["transaction_info"] as JObject;
                var payerInfo = detail["payer_info"] as JObject;
                if (txInfo == null)
                {
                    result.SkippedFiltered++;
                    continue;
                }

                var status = txInfo.Value<string>("transaction_status");
                if (!string.Equals(status, "S", StringComparison.Ordinal))
                {
                    result.SkippedFiltered++;
                    continue;
                }

                var txId = txInfo.Value<string>("transaction_id");
                if (string.IsNullOrWhiteSpace(txId))
                {
                    result.SkippedFiltered++;
                    continue;
                }

                var gross = txInfo["transaction_amount"]?["value"]?.Value<string>();
                if (
                    !decimal.TryParse(gross, NumberStyles.Any, CultureInfo.InvariantCulture, out var grossAmount)
                    || grossAmount <= 0
                )
                {
                    result.SkippedFiltered++;
                    continue;
                }

                var payerEmail = payerInfo?.Value<string>("email_address")?.Trim();
                if (string.IsNullOrEmpty(payerEmail))
                {
                    result.SkippedNoPayerEmail++;
                    continue;
                }

                var initiation = txInfo.Value<string>("transaction_initiation_date");
                DateTime? when = null;
                if (
                    !string.IsNullOrEmpty(initiation)
                    && DateTime.TryParse(
                        initiation,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                        out var parsed
                    )
                )
                {
                    when = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
                }

                eligible.Add(new EligiblePayPalRow(detail, txId, payerEmail, grossAmount, when));
            }

            var existingTxIds = await _payments
                .GetExistingPayPalTransactionIdsAsync(eligible.Select(e => e.TxId).ToList())
                .ConfigureAwait(false);

            var txIdsAccountedFor = new HashSet<string>(existingTxIds, StringComparer.Ordinal);

            foreach (var row in eligible)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!txIdsAccountedFor.Add(row.TxId))
                {
                    result.SkippedDuplicate++;
                    continue;
                }

                var normalized = UserEmailHelpers.NormalizeEmail(row.PayerEmail);
                string linkedUniqueId = null;
                if (emailToUniqueId.TryGetValue(normalized, out var uid))
                {
                    linkedUniqueId = uid;
                }

                var homeId = homeLookup.GetPrimaryHomeId(normalized);

                var payerInfo = row.Detail["payer_info"] as JObject;
                var displayName = FormatPayerName(payerInfo);

                var payment = new Payment
                {
                    PayerUniqueId = linkedUniqueId,
                    HomeId = homeId,
                    PayerEmail = row.PayerEmail,
                    PayerName = displayName,
                    Amount = row.GrossAmount.ToString(CultureInfo.InvariantCulture),
                    Date = row.When,
                    PaymentType = Payment.Type.PayPalSync,
                    FullDetailsJSON = row.Detail.ToString(Newtonsoft.Json.Formatting.None),
                    PayPalTransactionId = row.TxId,
                };

                await _payments.AddAsync(payment).ConfigureAwait(false);
                result.Inserted++;
                if (homeId == null && linkedUniqueId == null)
                {
                    result.InsertedUnlinked++;
                }
            }

            result.ExistingLinked = await LinkExistingUnlinkedPaymentsAsync(
                    emailToUniqueId,
                    homeLookup,
                    startUtc,
                    cancellationToken
                )
                .ConfigureAwait(false);

            _logger.LogInformation(
                "PayPal sync finished: read={Read}, inserted={Inserted}, insertedUnlinked={Unlinked}, duplicates={Dup}, existingLinked={Linked}, filtered={Filtered}, noPayerEmail={NoEmail}",
                result.TransactionsRead,
                result.Inserted,
                result.InsertedUnlinked,
                result.SkippedDuplicate,
                result.ExistingLinked,
                result.SkippedFiltered,
                result.SkippedNoPayerEmail
            );

            return result;
        }

        /// <summary>
        /// Updates payments already in Cosmos that have no <see cref="Payment.HomeId"/> when directory data now allows
        /// resolving a home (and optionally a user) from <see cref="Payment.PayerEmail"/>.
        /// </summary>
        private async Task<int> LinkExistingUnlinkedPaymentsAsync(
            IReadOnlyDictionary<string, string> emailToUniqueId,
            PayerEmailHomeLookup homeLookup,
            DateTime syncLookbackStartUtc,
            CancellationToken cancellationToken
        )
        {
            var unlinked = await _payments
                .GetPayPalSyncPaymentsWithoutHomeIdAsync(syncLookbackStartUtc)
                .ConfigureAwait(false);
            var linkedCount = 0;

            foreach (var payment in unlinked)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(payment.PayerEmail))
                {
                    continue;
                }

                var normalized = UserEmailHelpers.NormalizeEmail(payment.PayerEmail);
                if (normalized.Length == 0)
                {
                    continue;
                }

                var newHomeId = homeLookup.GetPrimaryHomeId(normalized);
                emailToUniqueId.TryGetValue(normalized, out var newUniqueId);

                var changed = false;
                if (newHomeId != null && payment.HomeId != newHomeId)
                {
                    payment.HomeId = newHomeId;
                    changed = true;
                }

                if (!string.IsNullOrEmpty(newUniqueId) && string.IsNullOrEmpty(payment.PayerUniqueId))
                {
                    payment.PayerUniqueId = newUniqueId;
                    changed = true;
                }

                if (!changed)
                {
                    continue;
                }

                await _payments.ReplaceAsync(payment).ConfigureAwait(false);
                linkedCount++;
            }

            return linkedCount;
        }

        private static Dictionary<string, string> BuildEmailIndex(IReadOnlyList<User> users)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var u in users)
            {
                foreach (var raw in UserEmailHelpers.SplitEmails(u.Emails))
                {
                    var key = UserEmailHelpers.NormalizeEmail(raw);
                    if (key.Length == 0)
                    {
                        continue;
                    }

                    if (!map.ContainsKey(key))
                    {
                        map[key] = u.UniqueId;
                    }
                }
            }

            return map;
        }

        private static string FormatPayerName(JObject payerInfo)
        {
            if (payerInfo == null)
            {
                return string.Empty;
            }

            var name = payerInfo["payer_name"] as JObject;
            if (name != null)
            {
                var alt = name.Value<string>("alternate_full_name");
                if (!string.IsNullOrWhiteSpace(alt))
                {
                    return alt.Trim();
                }

                var given = name.Value<string>("given_name") ?? "";
                var surname = name.Value<string>("surname") ?? "";
                var combined = $"{given} {surname}".Trim();
                if (combined.Length > 0)
                {
                    return combined;
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// Caches <see cref="PaymentHomeAssociation.FindHomeIdsForPayerEmail"/> per normalized email for a sync run.
        /// </summary>
        private sealed class PayerEmailHomeLookup
        {
            private readonly IReadOnlyList<Home> _homes;
            private readonly IReadOnlyDictionary<Guid, List<Resident>> _residentsByHome;
            private readonly IReadOnlyList<User> _users;
            private readonly Dictionary<string, IReadOnlyList<Guid>> _cache = new(StringComparer.OrdinalIgnoreCase);

            public PayerEmailHomeLookup(
                IReadOnlyList<Home> homes,
                IReadOnlyDictionary<Guid, List<Resident>> residentsByHome,
                IReadOnlyList<User> users
            )
            {
                _homes = homes;
                _residentsByHome = residentsByHome;
                _users = users;
            }

            public Guid? GetPrimaryHomeId(string normalizedEmail)
            {
                if (string.IsNullOrEmpty(normalizedEmail))
                {
                    return null;
                }

                if (!_cache.TryGetValue(normalizedEmail, out var homeIds))
                {
                    homeIds = PaymentHomeAssociation.FindHomeIdsForPayerEmail(
                        normalizedEmail,
                        _homes,
                        _residentsByHome,
                        _users
                    );
                    _cache[normalizedEmail] = homeIds;
                }

                return PaymentHomeAssociation.PickPrimaryHomeId(homeIds);
            }
        }

        private readonly struct EligiblePayPalRow
        {
            public EligiblePayPalRow(
                JObject detail,
                string txId,
                string payerEmail,
                decimal grossAmount,
                DateTime? when
            )
            {
                Detail = detail;
                TxId = txId;
                PayerEmail = payerEmail;
                GrossAmount = grossAmount;
                When = when;
            }

            public JObject Detail { get; }

            public string TxId { get; }

            public string PayerEmail { get; }

            public decimal GrossAmount { get; }

            public DateTime? When { get; }
        }
    }

    public sealed class PayPalPaymentSyncResult
    {
        public int TransactionsRead { get; set; }

        /// <summary>PayPal did not return a payer email on the row.</summary>
        public int SkippedNoPayerEmail { get; set; }

        public int SkippedDuplicate { get; set; }

        public int SkippedFiltered { get; set; }

        public int Inserted { get; set; }

        /// <summary>Subset of <see cref="Inserted"/> with no home or user match at sync time.</summary>
        public int InsertedUnlinked { get; set; }

        /// <summary>Rows that were already in the database without a home and were updated this run.</summary>
        public int ExistingLinked { get; set; }
    }
}
