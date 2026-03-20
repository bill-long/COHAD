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
using Web.UpdateModels;

namespace Web.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class MeController : ControllerBase
    {
        private readonly IUserRepository _userRepository;
        private readonly IHomeRepository _homeRepository;
        private readonly IEmailService _emailService;
        private readonly ILogger<MeController> _logger;

        public MeController(
            IUserRepository userRepository,
            IHomeRepository homeRepository,
            IEmailService emailService,
            ILogger<MeController> logger)
        {
            _userRepository = userRepository;
            _homeRepository = homeRepository;
            _emailService = emailService;
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

                // Includes are not supported, and we don't want this to be an owned type, so we're manually handling these references
                // See https://github.com/dotnet/efcore/issues/16920 for some of the issues with referenced types in Cosmos DB
                // See also https://docs.microsoft.com/en-us/ef/core/providers/cosmos/limitations
                var ownedHomes = new List<Home>();
                if (user.OwnedHomeIds != null && user.OwnedHomeIds.Count > 0)
                {
                    ownedHomes = await _homeRepository.GetByIdsAsync(user.OwnedHomeIds);
                    var allUsers = await _userRepository.GetAllAsync();
                    PopulateAssociatedUsers(ownedHomes, allUsers);
                }
                return PresentationUser.FromStorageModel(user, ownedHomes);
            }

            var newUser = new User
            {
                NameIdentifier = User.Claims.First(c => c.Type == ClaimTypes.NameIdentifier).Value,
                GivenName = User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.GivenName)?.Value,
                Surname = User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Surname)?.Value,
                IdentityProvider = User.Claims.First(c => c.Type == "http://schemas.microsoft.com/identity/claims/identityprovider").Value,
                Emails = User.Claims.FirstOrDefault(c => c.Type == "emails")?.Value,
                StreetAddress = User.Claims.FirstOrDefault(c => c.Type == "streetAddress")?.Value,
                Roles = new List<User.Role>(),
                OwnedHomeIds = new List<System.Guid>(),
                UniqueId = uniqueId,
                LastLoggedIn = DateTime.UtcNow
            };

            await _userRepository.UpsertAsync(newUser);

            FireAndForget(() => _emailService.SendEmail(
                "webservice@cohad.org",
                "COHAD Web",
                new EmailInfo
                {
                    Subject = "New User Registered",
                    HtmlBody = $"<div>Name: {newUser.GivenName} {newUser.Surname}</div><div>Email: {newUser.Emails}</div><div>Address: {newUser.StreetAddress}</div>"
                },
                new List<string> { "directory@cohad.org" },
                User));

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
                        IdentityProvider = u.IdentityProvider
                    })
                    .ToList();
            }
        }
    }
}
