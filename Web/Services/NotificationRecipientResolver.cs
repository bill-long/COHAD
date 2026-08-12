#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
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
        private readonly IResidentRepository _residentRepository;
        private readonly ICommitteeRepository _committeeRepository;
        private readonly ILogger<NotificationRecipientResolver> _logger;

        // The resolver is registered scoped and the escalation sweep creates one scope per run, so a
        // single instance resolves every audience in a sweep. Cache the (expensive) full user list once
        // per instance rather than re-scanning for the Administrators audience and each committee.
        private Task<List<Models.User>>? _allUsersTask;

        public NotificationRecipientResolver(
            IUserRepository userRepository,
            IResidentRepository residentRepository,
            ICommitteeRepository committeeRepository,
            ILogger<NotificationRecipientResolver> logger
        )
        {
            _userRepository = userRepository;
            _residentRepository = residentRepository;
            _committeeRepository = committeeRepository;
            _logger = logger;
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
        /// Maps a set of users to deliverable email addresses (distinct, case-insensitive). A user with
        /// an explicit resident link (<see cref="Models.User.ResidentId"/>) gets that resident's first
        /// non-blank address; everyone else gets their first account email. There is deliberately no
        /// email/name matching against resident records - inferred correlation silently dropped audience
        /// members whose records disagreed (see issue #15); the link is set by a human or not at all.
        /// A dangling or out-of-home link falls back to the account email, so a stale link can never
        /// make anyone worse off than an unlinked account. Shared by the Administrators and
        /// committee-moderator resolution so both reach people the same way.
        /// </summary>
        private async Task<IReadOnlyList<string>> ResolveUserEmailsAsync(List<Models.User> users)
        {
            if (users.Count == 0)
                return Array.Empty<string>();

            var linkedResidentIds = users
                .Where(u => u.ResidentId != null)
                .Select(u => u.ResidentId!.Value)
                .Distinct()
                .ToList();
            var linkedResidents =
                linkedResidentIds.Count > 0
                    ? await _residentRepository.GetByIdsAsync(linkedResidentIds)
                    : new List<Resident>();
            var residentsById = linkedResidents.ToDictionary(r => r.Id);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();

            foreach (var user in users)
            {
                var resolvedEmail = ResolveLinkedResidentEmail(user, residentsById)
                    ?? UserEmailHelpers.SplitEmails(user.Emails).FirstOrDefault();

                if (string.IsNullOrWhiteSpace(resolvedEmail))
                {
                    // One member silently falling out of an audience is exactly the failure mode that
                    // made issue #15 undiagnosable from telemetry; Warning is the app's capture level.
                    // Opaque id only - logs flow to Application Insights, whose stance is no PII.
                    _logger.LogWarning(
                        "Audience member {UserId} resolved to no email address and will not receive escalation digests.",
                        user.UniqueId
                    );
                    continue;
                }

                if (seen.Add(resolvedEmail))
                    result.Add(resolvedEmail);
            }

            return result;
        }

        /// <summary>
        /// The linked resident's delivery address, or null when the user has no usable link
        /// (<see cref="ResidentLinkRules.IsUsable"/> fails, or the resident lists no addresses) -
        /// callers fall back to the account email. Among the resident's addresses, one that is also
        /// an account address of the user wins, else the first non-blank: a resident record often
        /// lists a whole household's addresses, and when the account holder's own mailbox is among
        /// them it is the right destination, not whichever address happens to be listed first.
        /// </summary>
        private static string? ResolveLinkedResidentEmail(Models.User user, Dictionary<Guid, Resident> residentsById)
        {
            if (user.ResidentId == null || !residentsById.TryGetValue(user.ResidentId.Value, out var resident))
                return null;

            if (!ResidentLinkRules.IsUsable(resident, user.OwnedHomeIds))
                return null;

            var addresses = (resident.EmailAddresses ?? new List<EmailAddress>())
                .Select(e => e.Address?.Trim())
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .ToList();

            var accountEmails = new HashSet<string>(UserEmailHelpers.SplitEmails(user.Emails), StringComparer.OrdinalIgnoreCase);
            return addresses.FirstOrDefault(a => accountEmails.Contains(a!)) ?? addresses.FirstOrDefault();
        }
    }
}
