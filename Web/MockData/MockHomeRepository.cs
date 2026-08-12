using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Web.Models;
using Web.Services.Repositories;

namespace Web.MockData
{
    public sealed class MockHomeRepository : IHomeRepository
    {
        private readonly Dictionary<Guid, Home> _homes = new();

        // Simulated document versions backing ETag checks; guarded by the _homes lock.
        private readonly MockVersionMap<Guid> _versions = new();

        public MockHomeRepository()
        {
            var primaryHome = new Home
            {
                Id = MockDataConstants.SampleHomeId,
                StreetNumber = 123,
                StreetName = "Mock Lane",
                PhoneNumber = new PhoneNumber
                {
                    AreaCode = 555,
                    Prefix = 123,
                    LineNumber = 4567,
                    Type = "Home",
                    VisibleInDirectory = true,
                },
                EmailAddress = new EmailAddress
                {
                    Address = "home@cohad.local",
                    VisibleInDirectory = true,
                    BoardEmailOptedIn = true,
                    WelcomeEmailOptedIn = false,
                    GardenClubEmailOptedIn = false,
                    SocialCommitteeEmailOptedIn = false,
                    SunshineCommitteeEmailOptedIn = false,
                },
                // Residents are stored in the separate MockResidentRepository.
                AssociatedUsers = new List<HomeAssociatedUser>(),
            };
            _homes[primaryHome.Id] = CloneHome(primaryHome);
            _versions.Advance(primaryHome.Id);

            var secondaryHome = new Home
            {
                Id = MockDataConstants.SecondSampleHomeId,
                StreetNumber = 456,
                StreetName = "Test Court",
                PhoneNumber = new PhoneNumber
                {
                    AreaCode = 555,
                    Prefix = 987,
                    LineNumber = 6543,
                    Type = "Home",
                    VisibleInDirectory = true,
                },
                EmailAddress = new EmailAddress
                {
                    Address = "neighbor@cohad.local",
                    VisibleInDirectory = true,
                    BoardEmailOptedIn = true,
                    WelcomeEmailOptedIn = false,
                    GardenClubEmailOptedIn = false,
                    SocialCommitteeEmailOptedIn = false,
                    SunshineCommitteeEmailOptedIn = false,
                },
                // Residents are stored in the separate MockResidentRepository.
                AssociatedUsers = new List<HomeAssociatedUser>(),
            };
            _homes[secondaryHome.Id] = CloneHome(secondaryHome);
            _versions.Advance(secondaryHome.Id);
        }

        public Task<List<Home>> GetAllAsync()
        {
            lock (_homes)
            {
                return Task.FromResult(_homes.Values.Select(CloneWithETag).ToList());
            }
        }

        public Task<Home> GetByIdAsync(Guid id)
        {
            lock (_homes)
            {
                if (!_homes.TryGetValue(id, out var h))
                    return Task.FromResult<Home>(null);

                return Task.FromResult(CloneWithETag(h));
            }
        }

        public Task<List<Home>> GetByIdsAsync(List<Guid> ids)
        {
            lock (_homes)
            {
                var list = new List<Home>();
                foreach (var id in ids)
                {
                    if (_homes.TryGetValue(id, out var h))
                    {
                        list.Add(CloneWithETag(h));
                    }
                }

                return Task.FromResult(list);
            }
        }

        public Task<List<Home>> GetByEmailAsync(string email)
        {
            lock (_homes)
            {
                return Task.FromResult(
                    _homes
                        .Values.Where(h =>
                            string.Equals(h.EmailAddress?.Address?.Trim(), email, StringComparison.OrdinalIgnoreCase)
                        )
                        .Select(CloneWithETag)
                        .ToList()
                );
            }
        }

        public Task<Home> UpsertAsync(Home home)
        {
            lock (_homes)
            {
                _versions.ThrowIfStale(home.Id, home.ETag, "Home");

                _homes[home.Id] = CloneHome(home);

                // Match CosmosHomeRepository.UpsertAsync: mutate the caller's instance ETag in place and
                // return that same instance, so a caller reusing a Home across sequential upserts sees the
                // fresh ETag without recapturing the return value (the stored copy above is a defensive clone).
                home.ETag = _versions.Advance(home.Id);
                return Task.FromResult(home);
            }
        }

        // Clone a stored home and stamp its ETag from the version map, so every read path carries
        // ETag - matching CosmosHomeRepository/ToHome. Callers must hold the _homes lock (which
        // also guards _versions).
        private Home CloneWithETag(Home h)
        {
            var clone = CloneHome(h);
            clone.ETag = _versions.GetETag(h.Id);
            return clone;
        }

        private static Home CloneHome(Home h)
        {
            return new Home
            {
                Id = h.Id,
                StreetNumber = h.StreetNumber,
                StreetName = h.StreetName,
                PhoneNumber = ClonePhoneNumber(h.PhoneNumber),
                EmailAddress = CloneEmailAddress(h.EmailAddress),
                // Residents are not stored on the Home — they live in IResidentRepository.
                AssociatedUsers =
                    h.AssociatedUsers?.Select(CloneAssociatedUser).ToList() ?? new List<HomeAssociatedUser>(),
            };
        }

        private static PhoneNumber ClonePhoneNumber(PhoneNumber p)
        {
            if (p == null)
            {
                return null;
            }

            return new PhoneNumber
            {
                AreaCode = p.AreaCode,
                Prefix = p.Prefix,
                LineNumber = p.LineNumber,
                Type = p.Type,
                VisibleInDirectory = p.VisibleInDirectory,
            };
        }

        private static EmailAddress CloneEmailAddress(EmailAddress e)
        {
            if (e == null)
            {
                return null;
            }

            return new EmailAddress
            {
                Address = e.Address,
                VisibleInDirectory = e.VisibleInDirectory,
                BoardEmailOptedIn = e.BoardEmailOptedIn,
                WelcomeEmailOptedIn = e.WelcomeEmailOptedIn,
                GardenClubEmailOptedIn = e.GardenClubEmailOptedIn,
                SocialCommitteeEmailOptedIn = e.SocialCommitteeEmailOptedIn,
                SunshineCommitteeEmailOptedIn = e.SunshineCommitteeEmailOptedIn,
            };
        }

        private static HomeAssociatedUser CloneAssociatedUser(HomeAssociatedUser u)
        {
            if (u == null)
            {
                return null;
            }

            return new HomeAssociatedUser
            {
                UniqueId = u.UniqueId,
                GivenName = u.GivenName,
                Surname = u.Surname,
                Emails = u.Emails,
                IdentityProvider = u.IdentityProvider,
            };
        }
    }
}
