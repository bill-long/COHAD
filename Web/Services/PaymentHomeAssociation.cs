using System;
using System.Collections.Generic;
using System.Linq;
using Web.Models;

namespace Web.Services
{
    /// <summary>
    /// Maps PayPal payer emails to homes using resident emails, home emails, associated users, and user accounts that own homes.
    /// </summary>
    public static class PaymentHomeAssociation
    {
        /// <summary>
        /// For each home, all normalized email addresses that count as "on" that home for dues visibility.
        /// </summary>
        public static Dictionary<Guid, HashSet<string>> BuildEmailSetsByHome(IEnumerable<Home> homes)
        {
            var map = new Dictionary<Guid, HashSet<string>>();
            foreach (var home in homes)
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                AddEmail(set, home.EmailAddress?.Address);

                foreach (var resident in home.Residents ?? Enumerable.Empty<Resident>())
                {
                    foreach (var e in resident.EmailAddresses ?? Enumerable.Empty<EmailAddress>())
                    {
                        AddEmail(set, e?.Address);
                    }
                }

                foreach (var au in home.AssociatedUsers ?? Enumerable.Empty<HomeAssociatedUser>())
                {
                    foreach (var raw in UserEmailHelpers.SplitEmails(au?.Emails))
                    {
                        AddEmail(set, raw);
                    }
                }

                map[home.Id] = set;
            }

            return map;
        }

        /// <summary>
        /// Adds each user&apos;s account emails to the sets for homes they own so
        /// <see cref="PayerEmailStillOnHome"/> stays aligned with <see cref="FindHomeIdsForPayerEmail"/>
        /// when home documents do not list every owner in <see cref="Home.AssociatedUsers"/>.
        /// </summary>
        public static void MergeAccountEmailsFromUsersIntoHomeSets(
            IEnumerable<User> users,
            IReadOnlyCollection<Guid> homeIds,
            Dictionary<Guid, HashSet<string>> emailSetsByHome)
        {
            if (users == null || homeIds == null || homeIds.Count == 0)
            {
                return;
            }

            var idSet = new HashSet<Guid>(homeIds);
            foreach (var user in users)
            {
                foreach (var hid in user.OwnedHomeIds ?? Enumerable.Empty<Guid>())
                {
                    if (!idSet.Contains(hid))
                    {
                        continue;
                    }

                    if (!emailSetsByHome.TryGetValue(hid, out var set))
                    {
                        set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        emailSetsByHome[hid] = set;
                    }

                    foreach (var raw in UserEmailHelpers.SplitEmails(user.Emails))
                    {
                        AddEmail(set, raw);
                    }
                }
            }
        }

        /// <summary>
        /// All homes where <paramref name="normalizedEmail"/> appears on a resident, home email, associated user,
        /// or on a user account that lists <paramref name="home.Id"/> in <see cref="User.OwnedHomeIds"/>.
        /// </summary>
        public static IReadOnlyList<Guid> FindHomeIdsForPayerEmail(
            string normalizedEmail,
            IReadOnlyList<Home> homes,
            IReadOnlyList<User> users)
        {
            if (string.IsNullOrEmpty(normalizedEmail))
            {
                return Array.Empty<Guid>();
            }

            var found = new HashSet<Guid>();
            var emailSets = BuildEmailSetsByHome(homes);
            foreach (var kv in emailSets)
            {
                if (kv.Value.Contains(normalizedEmail))
                {
                    found.Add(kv.Key);
                }
            }

            foreach (var user in users)
            {
                foreach (var raw in UserEmailHelpers.SplitEmails(user.Emails))
                {
                    if (UserEmailHelpers.NormalizeEmail(raw) != normalizedEmail)
                    {
                        continue;
                    }

                    foreach (var hid in user.OwnedHomeIds ?? Enumerable.Empty<Guid>())
                    {
                        found.Add(hid);
                    }
                }
            }

            return found.OrderBy(g => g).ToList();
        }

        /// <summary>
        /// Single home to store on a payment when multiple match (deterministic).
        /// </summary>
        public static Guid? PickPrimaryHomeId(IReadOnlyList<Guid> homeIds) =>
            homeIds == null || homeIds.Count == 0 ? (Guid?)null : homeIds[0];

        public static bool PayerEmailStillOnHome(
            string payerEmail,
            Guid homeId,
            IReadOnlyDictionary<Guid, HashSet<string>> emailSetsByHome)
        {
            var n = UserEmailHelpers.NormalizeEmail(payerEmail);
            if (n.Length == 0)
            {
                return false;
            }

            return emailSetsByHome.TryGetValue(homeId, out var set) && set.Contains(n);
        }

        private static void AddEmail(HashSet<string> set, string raw)
        {
            var n = UserEmailHelpers.NormalizeEmail(raw ?? string.Empty);
            if (n.Length > 0)
            {
                set.Add(n);
            }
        }
    }
}
