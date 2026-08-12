using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Web.Models;
using Web.PresentationModels;
using Web.Services;
using Web.Services.Repositories;

namespace Web.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class MeController : ControllerBase
    {
        /// <summary>
        /// How stale the login stamp may get before this endpoint rewrites it. Coarse on purpose:
        /// the value is shown as "last logged in" in Manage Users, where the hour matters and the
        /// second does not, and every refresh is a write of the caller's own document.
        /// </summary>
        private static readonly TimeSpan LoginStampInterval = TimeSpan.FromHours(1);

        /// <summary>
        /// Whether the stored login stamp is old enough to rewrite. A stamp ahead of "now" counts
        /// as stale: legacy documents can hand back a local time that reads as a future UTC value,
        /// and clocks skew. Since this check is the only thing that triggers the write for a user
        /// whose claims are stable, treating a future stamp as fresh would freeze "last logged in"
        /// forever for an actively signing-in user.
        /// </summary>
        private static bool LoginStampIsStale(DateTime? lastLoggedIn)
        {
            if (lastLoggedIn == null)
            {
                return true;
            }

            // DateTime subtraction compares ticks and ignores Kind, so this is deliberately a
            // magnitude check in both directions rather than a Kind conversion.
            var elapsed = DateTime.UtcNow - lastLoggedIn.Value;
            return elapsed >= LoginStampInterval || elapsed < TimeSpan.Zero;
        }

        private readonly IUserRepository _userRepository;
        private readonly IHomeRepository _homeRepository;
        private readonly IResidentRepository _residentRepository;
        private readonly INotificationService _notificationService;
        private readonly ILogger<MeController> _logger;

        public MeController(
            IUserRepository userRepository,
            IHomeRepository homeRepository,
            IResidentRepository residentRepository,
            INotificationService notificationService,
            ILogger<MeController> logger
        )
        {
            _userRepository = userRepository;
            _homeRepository = homeRepository;
            _residentRepository = residentRepository;
            _notificationService = notificationService;
            _logger = logger;
        }

        [HttpGet]
        public async Task<PresentationUser> Get()
        {
            // Read directly rather than through the accessor: this action mutates the user it gets and
            // hands it to a background upsert, and the accessor's instance is shared with everything
            // else in the request. Nothing else reads the caller on this path - no role policy guards
            // it - so owning the instance costs no extra read.
            var uniqueId = Models.User.GetUniqueIdFromClaims(User.Claims);
            var user = await _userRepository.GetByUniqueIdAsync(uniqueId);
            if (user != null)
            {
                // A claim the token does not carry means "no information", not "cleared". A B2C
                // policy change or an identity provider that stops returning `emails` would
                // otherwise wipe the account's directory address, which nothing can recover - and
                // that missing claim is exactly what would trigger the write below.
                var givenName =
                    User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.GivenName)?.Value ?? user.GivenName;
                var surname = User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Surname)?.Value ?? user.Surname;
                var emails = User.Claims.FirstOrDefault(c => c.Type == "emails")?.Value ?? user.Emails;

                // This endpoint runs on every page load, not just at sign-in, so persist only when
                // there is something to persist: a changed B2C claim, or a login stamp that has
                // gone stale. An unconditional write would put an ETag-guarded write of the
                // caller's own document behind every page view, which then loses races against
                // that same user's foreground saves (a spurious 409 on their own profile edit) and
                // inflates the resident-link backfill's "changed during run" count.
                var claimsChanged =
                    !string.Equals(user.GivenName, givenName, StringComparison.Ordinal)
                    || !string.Equals(user.Surname, surname, StringComparison.Ordinal)
                    || !string.Equals(user.Emails, emails, StringComparison.Ordinal);
                var loginStampStale = LoginStampIsStale(user.LastLoggedIn);

                // Normalize unconditionally, before the response is built: UpsertAsync applies this
                // same rule (it drops the roles of a user with no homes), so applying it only on
                // the refreshing page loads would make the role set this endpoint reports depend on
                // whether the stamp happened to be stale - the same account's navigation appearing
                // and disappearing between loads. Apply is idempotent, so the write repeats it
                // harmlessly; if it changed anything the document is not normalized yet, which is
                // itself a reason to write so the stored state converges.
                // Apply reports whether it changed anything, including the purge clocks it can
                // start without changing either list's length (an Administrator with no homes keeps
                // their one role while UnassociatedSinceUtc goes from null to now). Reporting a
                // purge clock that was never stored would mean the deletion countdown never starts.
                var normalized = UserAssociationState.Apply(user);

                var refreshNeeded = claimsChanged || loginStampStale || normalized;
                if (refreshNeeded)
                {
                    user.GivenName = givenName;
                    user.Surname = surname;
                    user.Emails = emails;
                    user.LastLoggedIn = DateTime.UtcNow;
                }

                PresentationUser presentation;
                try
                {
                    var ownedHomes = new List<Home>();
                    if (user.HasResidentAccess && user.OwnedHomeIds != null && user.OwnedHomeIds.Count > 0)
                    {
                        // Includes are not supported, and we don't want this to be an owned type, so we're manually handling these references
                        // See https://github.com/dotnet/efcore/issues/16920 for some of the issues with referenced types in Cosmos DB
                        // See also https://docs.microsoft.com/en-us/ef/core/providers/cosmos/limitations
                        var homesTask = _homeRepository.GetByIdsAsync(user.OwnedHomeIds);
                        var residentsTask = _residentRepository.GetByHomeIdsAsync(user.OwnedHomeIds);
                        var allUsersTask = _userRepository.GetAllAsync();
                        try
                        {
                            await Task.WhenAll(homesTask, residentsTask, allUsersTask);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            // Deliberately empty: the awaits below rethrow the first failure after
                            // every task has completed (mirrors UpdateUserAssociations' join).
                        }

                        ownedHomes = await homesTask;
                        var ownedResidents = await residentsTask;
                        var allUsers = await allUsersTask;
                        PopulateAssociatedUsers(ownedHomes, allUsers);
                        PopulateResidents(ownedHomes, ownedResidents);
                    }

                    // Build the response model before firing the refresh: UpsertAsync mutates the
                    // instance it is handed (association-state stamps, the fresh ETag), and
                    // FromStorageModel copies everything it needs, so ordering it first keeps the
                    // background write from racing this request's serialization.
                    presentation = PresentationUser.FromStorageModel(user, ownedHomes);
                }
                finally
                {
                    // Fire even when the enrichment reads fail: authentication itself succeeded, so
                    // a failed homes lookup must not lose the claims sync or the LastLoggedIn
                    // stamp. On that failure path nothing serializes the user instance (the request
                    // errors), so the background mutation still cannot race a response.
                    if (refreshNeeded)
                    {
                        FireAndForget(() => RefreshLoginSnapshotAsync(user));
                    }
                }

                return presentation;
            }

            var newUser = new User
            {
                NameIdentifier =
                    User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value ?? string.Empty,
                GivenName = User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.GivenName)?.Value,
                Surname = User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Surname)?.Value,
                IdentityProvider =
                    User.Claims.FirstOrDefault(c =>
                        c.Type == "http://schemas.microsoft.com/identity/claims/identityprovider"
                    )?.Value
                    ?? string.Empty,
                Emails = User.Claims.FirstOrDefault(c => c.Type == "emails")?.Value,
                StreetAddress = User.Claims.FirstOrDefault(c => c.Type == "streetAddress")?.Value,
                Roles = new List<User.Role>(),
                OwnedHomeIds = new List<System.Guid>(),
                UniqueId = uniqueId,
                LastLoggedIn = DateTime.UtcNow,
            };

            await _userRepository.UpsertAsync(newUser);

            // Raise the unified in-app notification (the durable, first-resort signal). Email is no
            // longer sent inline here: NotificationEscalationService escalates unacknowledged
            // registrations to a throttled email digest once they age past the grace period.
            FireAndForget(() => RaiseNewUserNotification(newUser));

            return PresentationUser.FromStorageModel(newUser, new List<Home>());
        }

        /// <summary>
        /// Persists the login-time snapshot (B2C claims sync + LastLoggedIn) taken by
        /// <see cref="Get"/>. Losing a write race must not lose the claims sync - a changed sign-in
        /// email would otherwise stay stale in the directory until the next login - so a conflict
        /// is retried once against the fresh document. A missing document means the account was
        /// deleted concurrently (e.g. by the purge) and must not be resurrected by a login stamp.
        /// Failures log at Warning so a systematically losing refresh stays visible in production
        /// logs, which capture Warning and above.
        /// </summary>
        internal async Task RefreshLoginSnapshotAsync(User user)
        {
            try
            {
                await _userRepository.UpsertAsync(user);
                return;
            }
            catch (ConcurrencyConflictException)
            {
                // Fall through to the single fresh-read retry below.
            }

            var fresh = await _userRepository.GetByUniqueIdAsync(user.UniqueId);
            if (fresh == null)
            {
                _logger.LogWarning(
                    "Skipped the login-time refresh for user {UserId}: the account was deleted concurrently.",
                    user.UniqueId
                );
                return;
            }

            fresh.GivenName = user.GivenName;
            fresh.Surname = user.Surname;
            fresh.Emails = user.Emails;
            fresh.LastLoggedIn = user.LastLoggedIn;
            try
            {
                await _userRepository.UpsertAsync(fresh);
            }
            catch (ConcurrencyConflictException)
            {
                _logger.LogWarning(
                    "Login-time refresh for user {UserId} lost two concurrent-write races; the claims re-sync on the next login.",
                    user.UniqueId
                );
            }
        }

        private void FireAndForget(Func<Task> taskFactory)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await taskFactory();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Background side effect failed in MeController.");
                }
            });
        }

        internal async Task RaiseNewUserNotification(User newUser)
        {
            var name = $"{newUser.GivenName} {newUser.Surname}".Trim();
            var detail = new[] { name, newUser.StreetAddress, newUser.Emails }
                .Where(s => !string.IsNullOrWhiteSpace(s));
            var summary = string.Join(" — ", detail);

            await _notificationService.RaiseAsync(
                NotificationType.Registration,
                NotificationAudience.Administrators,
                NotificationTargetType.User,
                newUser.UniqueId,
                "New user registered",
                summary,
                "/manage/users"
            );
        }

        private static void PopulateAssociatedUsers(List<Home> homes, List<User> users)
        {
            foreach (var home in homes)
            {
                home.AssociatedUsers = users
                    .Where(u => u.OwnedHomeIds != null && u.OwnedHomeIds.Contains(home.Id))
                    .Select(u => new HomeAssociatedUser
                    {
                        UniqueId = u.UniqueId,
                        GivenName = u.GivenName,
                        Surname = u.Surname,
                        Emails = u.Emails,
                        IdentityProvider = u.IdentityProvider,
                    })
                    .ToList();
            }
        }

        private static void PopulateResidents(List<Home> homes, List<Resident> allResidents)
        {
            var byHomeId = allResidents.GroupBy(r => r.HomeId).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var home in homes)
            {
                home.Residents = byHomeId.TryGetValue(home.Id, out var residents) ? residents : new List<Resident>();
            }
        }
    }
}
