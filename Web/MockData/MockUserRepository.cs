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
            var u = new User
            {
                NameIdentifier = MockDataConstants.NameIdentifier,
                UniqueId = MockDataConstants.UniqueId,
                IdentityProvider = MockDataConstants.IdentityProvider,
                GivenName = "Mock",
                Surname = "Resident",
                Emails = "mock@cohad.local",
                StreetAddress = "123 Mock Lane",
                Roles = new List<User.Role> { User.Role.Resident, User.Role.Administrator },
                OwnedHomeIds = new List<Guid> { MockDataConstants.SampleHomeId },
                LastLoggedIn = DateTime.UtcNow.AddDays(-1)
            };
            UserAssociationState.Apply(u);
            _users[u.UniqueId] = u;
        }

        public Task<List<User>> GetAllAsync()
        {
            lock (_users)
            {
                return Task.FromResult(_users.Values.ToList());
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
                return Task.FromResult(_users.TryGetValue(uniqueId, out var u) ? u : null);
            }
        }

        public Task<User> UpsertAsync(User user)
        {
            UserAssociationState.Apply(user);
            lock (_users)
            {
                _users[user.UniqueId] = user;
            }

            return Task.FromResult(user);
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
    }
}
