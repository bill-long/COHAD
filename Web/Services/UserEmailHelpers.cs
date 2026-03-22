using System;
using System.Collections.Generic;
using Web.Models;

namespace Web.Services
{
    /// <summary>
    /// Normalizes and splits user email strings for matching PayPal payer emails.
    /// </summary>
    public static class UserEmailHelpers
    {
        public static string NormalizeEmail(string email)
        {
            return string.IsNullOrWhiteSpace(email) ? string.Empty : email.Trim().ToLowerInvariant();
        }

        public static IEnumerable<string> SplitEmails(string emails)
        {
            if (string.IsNullOrWhiteSpace(emails))
            {
                yield break;
            }

            foreach (var part in emails.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var t = part.Trim();
                if (t.Length > 0)
                {
                    yield return t;
                }
            }
        }

        /// <summary>
        /// True if <paramref name="payerEmail"/> matches any of <paramref name="user"/>'s emails (case-insensitive).
        /// </summary>
        public static bool EmailMatchesUser(string payerEmail, User user)
        {
            if (user == null || string.IsNullOrWhiteSpace(payerEmail))
            {
                return false;
            }

            var needle = NormalizeEmail(payerEmail);
            foreach (var e in SplitEmails(user.Emails))
            {
                if (NormalizeEmail(e) == needle)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
