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

        // The resolver is registered scoped and the escalation sweep creates one scope per run, so a
        // single instance resolves every audience in a sweep. Cache the (expensive) full user list once
        // per instance rather than re-scanning for the Administrators audience and each committee.
        private Task<List<Models.User>>? _allUsersTask;

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
                return await ResolveCommitteeModeratorsAsync(committeeId);

            return Array.Empty<string>();
        }

        /// <summary>Fetches all users at most once per resolver instance (i.e. once per sweep).</summary>
        private Task<List<Models.User>> GetAllUsersAsync() => _allUsersTask ??= _userRepository.GetAllAsync();

        /// <summary>
        /// Resolves Administrator emails — the users who can act on the Administrators audience in-app.
        /// </summary>
        private async Task<IReadOnlyList<string>> ResolveAdministratorsAsync()
        {
            var allUsers = await GetAllUsersAsync();
            var admins = allUsers
                .Where(u => u.Roles != null && u.Roles.Contains(Models.User.Role.Administrator))
                .ToList();
            return await ResolveUserEmailsAsync(admins);
        }

        /// <summary>
        /// Resolves a committee's moderator emails — the users who can act on the committee audience
        /// in-app, i.e. <see cref="CommitteeAuthorization.CanManage"/> (Administrators plus holders of the
        /// committee's <see cref="Committee.ManagementRole"/>). This deliberately matches the in-app
        /// audience rather than the committee's display membership, so escalation emails never leak
        /// held-message details to members who lack moderation access.
        /// </summary>
        private async Task<IReadOnlyList<string>> ResolveCommitteeModeratorsAsync(string committeeId)
        {
            var committee = await _committeeRepository.GetByIdAsync(committeeId);
            if (committee == null)
                return Array.Empty<string>();

            var allUsers = await GetAllUsersAsync();
            var moderators = allUsers.Where(u => CommitteeAuthorization.CanManage(u, committee)).ToList();
            return await ResolveUserEmailsAsync(moderators);
        }

        /// <summary>
        /// Maps a set of users to deliverable email addresses (distinct, case-insensitive). For each user
        /// with an owned home, prefers the resident address matching their account (by email, then by
        /// name); a home-less user falls back to their own account email. Shared by the Administrators and
        /// committee-moderator resolution so both reach people the same way.
        /// </summary>
        private async Task<IReadOnlyList<string>> ResolveUserEmailsAsync(List<Models.User> users)
        {
            if (users.Count == 0)
                return Array.Empty<string>();

            var allHomeIds = users
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

            foreach (var user in users)
            {
                // Home-less users can't be matched to a resident record, so fall back to their own
                // account email. Without this they would silently never receive escalations.
                if (user.OwnedHomeIds == null || user.OwnedHomeIds.Count == 0)
                {
                    var accountEmail = UserEmailHelpers.SplitEmails(user.Emails).FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(accountEmail) && seen.Add(accountEmail))
                        result.Add(accountEmail);
                    continue;
                }

                var userGivenName = user.GivenName?.Trim();
                var userSurname = user.Surname?.Trim();

                foreach (var homeId in user.OwnedHomeIds)
                {
                    if (!residentsByHome.TryGetValue(homeId, out var homeResidents))
                        continue;

                    // Try matching by email first — if a resident email matches one of the user's
                    // addresses, use that single matched address. user.Emails may hold several
                    // comma/semicolon-separated addresses, so match each individually (matching the
                    // whole blob as one string would never match and would yield an invalid recipient).
                    string? resolvedEmail = null;
                    foreach (var userAddress in UserEmailHelpers.SplitEmails(user.Emails))
                    {
                        var emailMatch = homeResidents.FirstOrDefault(r =>
                            r.EmailAddresses != null
                            && r.EmailAddresses.Any(e =>
                                string.Equals(e.Address?.Trim(), userAddress, StringComparison.OrdinalIgnoreCase)
                            )
                        );
                        if (emailMatch != null)
                        {
                            resolvedEmail = userAddress;
                            break;
                        }
                    }

                    // Fall back to name matching — use the resident's first non-empty email.
                    if (resolvedEmail == null)
                    {
                        var nameMatch = homeResidents.FirstOrDefault(r =>
                            string.Equals(r.GivenName?.Trim(), userGivenName, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(r.Surname?.Trim(), userSurname, StringComparison.OrdinalIgnoreCase)
                        );
                        resolvedEmail = nameMatch
                            ?.EmailAddresses?.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.Address))
                            ?.Address?.Trim();
                    }

                    if (string.IsNullOrWhiteSpace(resolvedEmail))
                        continue;

                    if (seen.Add(resolvedEmail))
                        result.Add(resolvedEmail);

                    break; // Found a match for this user, no need to check other homes.
                }
            }

            return result;
        }
    }
}
