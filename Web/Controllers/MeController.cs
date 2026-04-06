using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Text;
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
        private readonly IUserRepository _userRepository;
        private readonly IHomeRepository _homeRepository;
        private readonly IResidentRepository _residentRepository;
        private readonly IEmailJobRepository _emailJobRepository;
        private readonly IDocumentFileStore _fileStore;
        private readonly EmailJobQueue _emailJobQueue;
        private readonly EmailJobCleanupService _emailJobCleanup;
        private readonly ILogger<MeController> _logger;

        public MeController(
            IUserRepository userRepository,
            IHomeRepository homeRepository,
            IResidentRepository residentRepository,
            IEmailJobRepository emailJobRepository,
            IDocumentFileStore fileStore,
            EmailJobQueue emailJobQueue,
            EmailJobCleanupService emailJobCleanup,
            ILogger<MeController> logger
        )
        {
            _userRepository = userRepository;
            _homeRepository = homeRepository;
            _residentRepository = residentRepository;
            _emailJobRepository = emailJobRepository;
            _fileStore = fileStore;
            _emailJobQueue = emailJobQueue;
            _emailJobCleanup = emailJobCleanup;
            _logger = logger;
        }

        [HttpGet]
        public async Task<PresentationUser> Get()
        {
            var uniqueId = Models.User.GetUniqueIdFromClaims(User.Claims);
            var user = await _userRepository.GetByUniqueIdAsync(uniqueId);
            if (user != null)
            {
                user.GivenName = User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.GivenName)?.Value;
                user.Surname = User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Surname)?.Value;
                user.Emails = User.Claims.FirstOrDefault(c => c.Type == "emails")?.Value;
                user.LastLoggedIn = DateTime.UtcNow;
                FireAndForget(() => _userRepository.UpsertAsync(user));

                var ownedHomes = new List<Home>();
                if (user.HasResidentAccess && user.OwnedHomeIds != null && user.OwnedHomeIds.Count > 0)
                {
                    // Includes are not supported, and we don't want this to be an owned type, so we're manually handling these references
                    // See https://github.com/dotnet/efcore/issues/16920 for some of the issues with referenced types in Cosmos DB
                    // See also https://docs.microsoft.com/en-us/ef/core/providers/cosmos/limitations
                    ownedHomes = await _homeRepository.GetByIdsAsync(user.OwnedHomeIds);
                    var ownedResidents = await _residentRepository.GetByHomeIdsAsync(user.OwnedHomeIds);
                    var allUsers = await _userRepository.GetAllAsync();
                    PopulateAssociatedUsers(ownedHomes, allUsers);
                    PopulateResidents(ownedHomes, ownedResidents);
                }
                return PresentationUser.FromStorageModel(user, ownedHomes);
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

            FireAndForget(() => EnqueueNewUserNotification(newUser));

            return PresentationUser.FromStorageModel(newUser, new List<Home>());
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

        internal async Task EnqueueNewUserNotification(User newUser)
        {
            var recipients = await ResolveAdminRecipients();
            if (recipients.Count == 0)
            {
                _logger.LogWarning("No administrator recipients resolved for new user notification.");
                return;
            }

            var htmlBody =
                $"<div>Name: {WebUtility.HtmlEncode(newUser.GivenName)} {WebUtility.HtmlEncode(newUser.Surname)}</div>"
                + $"<div>Email: {WebUtility.HtmlEncode(newUser.Emails)}</div>"
                + $"<div>Address: {WebUtility.HtmlEncode(newUser.StreetAddress)}</div>";

            try
            {
                await _emailJobCleanup.RunOnceBestEffortAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Email job retention cleanup failed during new user notification (best-effort)."
                );
            }

            var job = new EmailJob
            {
                Id = Guid.NewGuid(),
                Status = EmailJobStatus.Queued,
                Category = "registration",
                FromEmail = "webservice@cohad.org",
                FromDisplay = "COHAD Web",
                Subject = "New User Registered",
                CreatedUtc = DateTime.UtcNow,
                CreatedByUserId = "system",
                CreatedByDisplayName = "System",
                TotalRecipients = recipients.Count,
                Recipients = recipients,
            };

            job.ContentBlobPath = $"email-jobs/{job.Id:D}.html";
            var jobPersisted = false;
            try
            {
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(htmlBody)))
                {
                    await _fileStore.UploadAsync(job.ContentBlobPath, stream, "text/html");
                }

                await _emailJobRepository.AddAsync(job);
                jobPersisted = true;
            }
            catch
            {
                if (!jobPersisted && !string.IsNullOrEmpty(job.ContentBlobPath))
                {
                    try
                    {
                        await _fileStore.DeleteAsync(job.ContentBlobPath);
                    }
                    catch
                    { /* best-effort cleanup */
                    }
                }

                throw;
            }

            try
            {
                await _emailJobQueue.EnqueueAsync(job.Id);
            }
            catch
            {
                try
                {
                    await _emailJobRepository.DeleteAsync(job.Id);
                }
                catch
                { /* best-effort cleanup */
                }

                if (!string.IsNullOrEmpty(job.ContentBlobPath))
                {
                    try
                    {
                        await _fileStore.DeleteAsync(job.ContentBlobPath);
                    }
                    catch
                    { /* best-effort cleanup */
                    }
                }

                throw;
            }
        }

        internal async Task<List<EmailJobRecipient>> ResolveAdminRecipients()
        {
            var allUsers = await _userRepository.GetAllAsync();
            var admins = allUsers
                .Where(u => u.Roles != null && u.Roles.Contains(Models.User.Role.Administrator))
                .ToList();

            if (admins.Count == 0)
                return new List<EmailJobRecipient>();

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
            var result = new List<EmailJobRecipient>();

            foreach (var admin in admins)
            {
                if (admin.OwnedHomeIds == null || admin.OwnedHomeIds.Count == 0)
                    continue;

                foreach (var homeId in admin.OwnedHomeIds)
                {
                    if (!residentsByHome.TryGetValue(homeId, out var homeResidents))
                        continue;

                    // Try matching by email first — if matched, use the matched address directly
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

                    // Fall back to name matching — use the resident's first non-empty email
                    if (resolvedEmail == null)
                    {
                        var nameMatch = homeResidents.FirstOrDefault(r =>
                            string.Equals(r.GivenName, admin.GivenName, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(r.Surname, admin.Surname, StringComparison.OrdinalIgnoreCase)
                        );
                        resolvedEmail = nameMatch
                            ?.EmailAddresses?.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.Address))
                            ?.Address?.Trim();
                    }

                    if (string.IsNullOrWhiteSpace(resolvedEmail))
                        continue;

                    if (seen.Add(resolvedEmail))
                    {
                        // Use Guid.Empty for HomeId — this is a transactional notification,
                        // not a subscription email, so unsubscribe headers should be suppressed.
                        result.Add(
                            new EmailJobRecipient
                            {
                                Email = resolvedEmail,
                                HomeId = Guid.Empty,
                                Status = EmailJobRecipientStatus.Pending,
                            }
                        );
                    }

                    break; // Found a match for this admin, no need to check other homes
                }
            }

            return result;
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
