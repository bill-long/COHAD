#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Web.Models;
using Web.Services.Repositories;

namespace Web.Services
{
    /// <summary>
    /// Resolves the email recipients for a notification audience key, used by the escalation sweeper to
    /// decide who receives a digest. Centralizes the matching so the audience that sees a notification
    /// in-app and the audience that gets emailed about it stay aligned.
    /// </summary>
    public interface INotificationRecipientResolver
    {
        /// <summary>
        /// Returns the distinct (case-insensitive) email addresses for the given audience key, or an
        /// empty list for an unknown/empty audience. Casing of the first occurrence is preserved.
        /// </summary>
        Task<IReadOnlyList<string>> ResolveAudienceEmailsAsync(string audienceKey, CancellationToken ct = default);
    }

    public sealed class NotificationRecipientResolver : INotificationRecipientResolver
    {
        private readonly IUserRepository _userRepository;
        private readonly IHomeRepository _homeRepository;
        private readonly IResidentRepository _residentRepository;
        private readonly ICommitteeRepository _committeeRepository;

        public NotificationRecipientResolver(
            IUserRepository userRepository,
            IHomeRepository homeRepository,
            IResidentRepository residentRepository,
            ICommitteeRepository committeeRepository
        )
        {
            _userRepository = userRepository;
            _homeRepository = homeRepository;
            _residentRepository = residentRepository;
            _committeeRepository = committeeRepository;
        }

        public async Task<IReadOnlyList<string>> ResolveAudienceEmailsAsync(string audienceKey, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(audienceKey))
                return Array.Empty<string>();

            if (string.Equals(audienceKey, NotificationAudience.Administrators, StringComparison.Ordinal))
                return await ResolveAdministratorsAsync();

            var committeeId = NotificationAudience.TryGetCommitteeId(audienceKey);
            if (committeeId != null)
                return await ResolveCommitteeMembersAsync(committeeId);

            return Array.Empty<string>();
        }

        /// <summary>
        /// Resolves Administrator emails by preferring the resident address that matches an admin's
        /// account (by email, then by name) for each owned home, and falling back to the admin's own
        /// account email when they have no associated home. Mirrors the previous inline registration
        /// notification matching so escalation reaches the same people.
        /// </summary>
        private async Task<IReadOnlyList<string>> ResolveAdministratorsAsync()
        {
            var allUsers = await _userRepository.GetAllAsync();
            var admins = allUsers
                .Where(u => u.Roles != null && u.Roles.Contains(Models.User.Role.Administrator))
                .ToList();

            if (admins.Count == 0)
                return Array.Empty<string>();

            var allHomeIds = admins
                .Where(u => u.OwnedHomeIds != null)
                .SelectMany(u => u.OwnedHomeIds)
                .Distinct()
                .ToList();

            var homes = allHomeIds.Count > 0 ? await _homeRepository.GetByIdsAsync(allHomeIds) : new List<Home>();
            var residentHomeIds = homes.Select(h => h.Id).ToList();
            var residents =
                residentHomeIds.Count > 0
                    ? await _residentRepository.GetByHomeIdsAsync(residentHomeIds)
                    : new List<Resident>();
            var residentsByHome = residents.GroupBy(r => r.HomeId).ToDictionary(g => g.Key, g => g.ToList());

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();

            foreach (var admin in admins)
            {
                // Home-less admins can't be matched to a resident record, so fall back to their own
                // account email. Without this they would silently never receive escalations.
                if (admin.OwnedHomeIds == null || admin.OwnedHomeIds.Count == 0)
                {
                    var accountEmail = UserEmailHelpers.SplitEmails(admin.Emails).FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(accountEmail) && seen.Add(accountEmail))
                        result.Add(accountEmail);
                    continue;
                }

                var adminGivenName = admin.GivenName?.Trim();
                var adminSurname = admin.Surname?.Trim();

                foreach (var homeId in admin.OwnedHomeIds)
                {
                    if (!residentsByHome.TryGetValue(homeId, out var homeResidents))
                        continue;

                    // Try matching by email first — if matched, use the matched address directly.
                    string? resolvedEmail = null;
                    if (!string.IsNullOrWhiteSpace(admin.Emails))
                    {
                        var emailMatch = homeResidents.FirstOrDefault(r =>
                            r.EmailAddresses != null
                            && r.EmailAddresses.Any(e =>
                                string.Equals(
                                    e.Address?.Trim(),
                                    admin.Emails.Trim(),
                                    StringComparison.OrdinalIgnoreCase
                                )
                            )
                        );
                        if (emailMatch != null)
                            resolvedEmail = admin.Emails.Trim();
                    }

                    // Fall back to name matching — use the resident's first non-empty email.
                    if (resolvedEmail == null)
                    {
                        var nameMatch = homeResidents.FirstOrDefault(r =>
                            string.Equals(r.GivenName?.Trim(), adminGivenName, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(r.Surname?.Trim(), adminSurname, StringComparison.OrdinalIgnoreCase)
                        );
                        resolvedEmail = nameMatch
                            ?.EmailAddresses?.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.Address))
                            ?.Address?.Trim();
                    }

                    if (string.IsNullOrWhiteSpace(resolvedEmail))
                        continue;

                    if (seen.Add(resolvedEmail))
                        result.Add(resolvedEmail);

                    break; // Found a match for this admin, no need to check other homes.
                }
            }

            return result;
        }

        /// <summary>
        /// Resolves a committee's member emails (the people listed on the committee), via each member's
        /// linked resident record. These are the recipients for held-committee-email escalations.
        /// </summary>
        private async Task<IReadOnlyList<string>> ResolveCommitteeMembersAsync(string committeeId)
        {
            var committee = await _committeeRepository.GetByIdAsync(committeeId);
            var members = committee?.Members;
            if (members == null || members.Count == 0)
                return Array.Empty<string>();

            var residentIds = members.Select(m => m.ResidentId).Distinct().ToList();
            var residents = await _residentRepository.GetByIdsAsync(residentIds);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            foreach (var resident in residents)
            {
                var email = resident.EmailAddresses
                    ?.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e?.Address))
                    ?.Address?.Trim();
                if (!string.IsNullOrWhiteSpace(email) && seen.Add(email))
                    result.Add(email);
            }

            return result;
        }
    }
}
