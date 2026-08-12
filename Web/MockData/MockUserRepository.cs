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

        // Simulated document versions backing ETag checks; guarded by the _users lock.
        private readonly MockVersionMap<string> _versions = new(StringComparer.Ordinal);

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
            _versions.Advance(adminUser.UniqueId);

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
            _versions.Advance(secondaryUser.UniqueId);
        }

        public Task<List<User>> GetAllAsync()
        {
            lock (_users)
            {
                return Task.FromResult(_users.Values.Select(CloneWithETag).ToList());
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
                return Task.FromResult(_users.TryGetValue(uniqueId, out var u) ? CloneWithETag(u) : null);
            }
        }

        public Task<User> UpsertAsync(User user)
        {
            // Match CosmosUserRepository.UpsertAsync, which applies association state to the caller's
            // instance before writing.
            UserAssociationState.Apply(user);
            lock (_users)
            {
                _versions.ThrowIfStale(user.UniqueId, user.ETag, "User");

                _users[user.UniqueId] = CloneUser(user);

                // Match CosmosUserRepository.UpsertAsync: mutate the caller's instance ETag in place and
                // return that same instance, so a caller reusing a User across sequential upserts sees
                // the fresh ETag without recapturing the return value (the stored copy above is a
                // defensive clone).
                user.ETag = _versions.Advance(user.UniqueId);
                return Task.FromResult(user);
            }
        }

        /// <summary>
        /// Mirrors <see cref="Web.Services.Repositories.CosmosUserRepository.GetPurgeCandidatesAsync"/>:
        /// users whose no-home or no-role clock is on or before the cutoff. Previously this returned an
        /// empty list unconditionally, which was harmless while the purge ran in a separate Function App
        /// but makes the in-process job impossible to exercise in the MockData environment.
        /// </summary>
        public Task<List<User>> GetPurgeCandidatesAsync(DateTime cutoffUtc)
        {
            lock (_users)
            {
                var candidates = _users
                    .Values.Where(u =>
                    {
                        var noHomesEligible =
                            (u.OwnedHomeIds == null || u.OwnedHomeIds.Count == 0)
                            && u.UnassociatedSinceUtc != null
                            && u.UnassociatedSinceUtc <= cutoffUtc;
                        var noRolesEligible =
                            (u.Roles == null || u.Roles.Count == 0)
                            && u.NoRolesSinceUtc != null
                            && u.NoRolesSinceUtc <= cutoffUtc;
                        return noHomesEligible || noRolesEligible;
                    })
                    .Select(CloneWithETag)
                    .ToList();

                return Task.FromResult(candidates);
            }
        }

        public Task DeleteAsync(string uniqueId)
        {
            lock (_users)
            {
                _users.Remove(uniqueId);
                _versions.Remove(uniqueId);
            }

            return Task.CompletedTask;
        }

        // Clone a stored user and stamp its ETag from the version map, so every read path carries
        // ETag - matching CosmosUserRepository/ToUser. Callers must hold the _users lock (which
        // also guards _versions).
        private User CloneWithETag(User u)
        {
            var clone = CloneUser(u);
            clone.ETag = _versions.GetETag(u.UniqueId);
            return clone;
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
                ResidentId = u.ResidentId,
                LastLoggedIn = u.LastLoggedIn,
                UnassociatedSinceUtc = u.UnassociatedSinceUtc,
                NoRolesSinceUtc = u.NoRolesSinceUtc,
            };
        }
    }
}
