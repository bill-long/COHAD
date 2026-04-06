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
        private readonly Dictionary<Guid, int> _versions = new();

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
            _versions[primaryHome.Id] = 1;

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
            _versions[secondaryHome.Id] = 1;
        }

        public Task<List<Home>> GetAllAsync()
        {
            lock (_homes)
            {
                return Task.FromResult(_homes.Values.Select(CloneHome).ToList());
            }
        }

        public Task<Home> GetByIdAsync(Guid id)
        {
            lock (_homes)
            {
                if (!_homes.TryGetValue(id, out var h))
                    return Task.FromResult<Home>(null);

                var clone = CloneHome(h);
                clone.ETag = _versions.TryGetValue(id, out var v) ? v.ToString() : null;
                return Task.FromResult(clone);
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
                        list.Add(CloneHome(h));
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
                        .Select(CloneHome)
                        .ToList()
                );
            }
        }

        public Task<Home> UpsertAsync(Home home)
        {
            lock (_homes)
            {
                // Optimistic concurrency: if ETag is provided, check it matches current version
                if (!string.IsNullOrEmpty(home.ETag) && _versions.TryGetValue(home.Id, out var currentVersion))
                {
                    if (home.ETag != currentVersion.ToString())
                    {
                        throw new ConcurrencyConflictException(
                            $"Home {home.Id} was modified by another request. Retry the operation.",
                            new InvalidOperationException("ETag mismatch")
                        );
                    }
                }

                _homes[home.Id] = CloneHome(home);
                _versions[home.Id] = (_versions.TryGetValue(home.Id, out var v) ? v : 0) + 1;
                var clone = CloneHome(_homes[home.Id]);
                clone.ETag = _versions[home.Id].ToString();
                return Task.FromResult(clone);
            }
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
