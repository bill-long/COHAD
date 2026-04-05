using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Web.Models;
using Web.Services;
using Web.Services.Repositories;

namespace Web.MockData
{
    public sealed class MockUserRepository : IUserRepository
    {
        private readonly Dictionary<string, User> _users = new(StringComparer.Ordinal);

        public MockUserRepository()
        {
            var adminUser = new User
            {
                NameIdentifier = MockDataConstants.AdminNameIdentifier,
                UniqueId = MockDataConstants.AdminUniqueId,
                IdentityProvider = MockDataConstants.IdentityProvider,
                GivenName = "Mock",
                Surname = "Resident",
                Emails = "mock@cohad.local",
                StreetAddress = "123 Mock Lane",
                Roles = new List<User.Role> { User.Role.Resident, User.Role.Administrator, User.Role.Board },
                OwnedHomeIds = new List<Guid> { MockDataConstants.SampleHomeId },
                LastLoggedIn = DateTime.UtcNow.AddDays(-1),
            };
            UserAssociationState.Apply(adminUser);
            _users[adminUser.UniqueId] = adminUser;

            var secondaryUser = new User
            {
                NameIdentifier = MockDataConstants.SecondaryUserNameIdentifier,
                UniqueId = MockDataConstants.SecondaryUserUniqueId,
                IdentityProvider = MockDataConstants.IdentityProvider,
                GivenName = "Taylor",
                Surname = "Neighbor",
                Emails = "taylor@cohad.local",
                StreetAddress = "456 Test Court",
                Roles = new List<User.Role> { User.Role.Resident },
                OwnedHomeIds = new List<Guid> { MockDataConstants.SecondSampleHomeId },
                LastLoggedIn = DateTime.UtcNow.AddDays(-2),
            };
            UserAssociationState.Apply(secondaryUser);
            _users[secondaryUser.UniqueId] = secondaryUser;
        }

        public Task<List<User>> GetAllAsync()
        {
            lock (_users)
            {
                return Task.FromResult(_users.Values.Select(CloneUser).ToList());
            }
        }

        public Task<User> GetByUniqueIdAsync(string uniqueId)
        {
            if (string.IsNullOrEmpty(uniqueId))
            {
                return Task.FromResult<User>(null);
            }

            lock (_users)
            {
                return Task.FromResult(_users.TryGetValue(uniqueId, out var u) ? CloneUser(u) : null);
            }
        }

        public Task<User> UpsertAsync(User user)
        {
            var copy = CloneUser(user);
            UserAssociationState.Apply(copy);
            lock (_users)
            {
                _users[copy.UniqueId] = copy;
                return Task.FromResult(CloneUser(copy));
            }
        }

        public Task<List<User>> GetPurgeCandidatesAsync(DateTime cutoffUtc, int maxCount)
        {
            return Task.FromResult(new List<User>());
        }

        public Task DeleteAsync(string uniqueId)
        {
            lock (_users)
            {
                _users.Remove(uniqueId);
            }

            return Task.CompletedTask;
        }

        private static User CloneUser(User u)
        {
            if (u == null)
            {
                return null;
            }

            return new User
            {
                NameIdentifier = u.NameIdentifier,
                GivenName = u.GivenName,
                Surname = u.Surname,
                IdentityProvider = u.IdentityProvider,
                Emails = u.Emails,
                StreetAddress = u.StreetAddress,
                Roles = u.Roles?.ToList() ?? new List<User.Role>(),
                UniqueId = u.UniqueId,
                OwnedHomeIds = u.OwnedHomeIds?.ToList() ?? new List<Guid>(),
                LastLoggedIn = u.LastLoggedIn,
                UnassociatedSinceUtc = u.UnassociatedSinceUtc,
                NoRolesSinceUtc = u.NoRolesSinceUtc,
            };
        }
    }
}
