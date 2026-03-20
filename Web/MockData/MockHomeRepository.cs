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

        public MockHomeRepository()
        {
            var home = new Home
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
                    VisibleInDirectory = true
                },
                EmailAddress = new EmailAddress
                {
                    Address = "home@cohad.local",
                    VisibleInDirectory = true,
                    BoardEmailOptedIn = false,
                    WelcomeEmailOptedIn = false,
                    GardenClubEmailOptedIn = false,
                    SocialCommitteeEmailOptedIn = false,
                    SunshineCommitteeEmailOptedIn = false
                },
                Residents = new List<Resident>
                {
                    new Resident
                    {
                        GivenName = "Mock",
                        Surname = "Resident",
                        YearOfBirth = 1980,
                        CollegeName = "",
                        ResidentType = Resident.Type.Homeowner,
                        EmailAddresses = new List<EmailAddress>
                        {
                            new EmailAddress
                            {
                                Address = "mock@cohad.local",
                                VisibleInDirectory = true,
                                BoardEmailOptedIn = false,
                                WelcomeEmailOptedIn = false,
                                GardenClubEmailOptedIn = false,
                                SocialCommitteeEmailOptedIn = false,
                                SunshineCommitteeEmailOptedIn = false
                            }
                        },
                        PhoneNumbers = new List<PhoneNumber>()
                    }
                },
                AssociatedUsers = new List<HomeAssociatedUser>()
            };
            _homes[home.Id] = home;
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
                return Task.FromResult(_homes.TryGetValue(id, out var h) ? CloneHome(h) : null);
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

        public Task<Home> UpsertAsync(Home home)
        {
            lock (_homes)
            {
                _homes[home.Id] = CloneHome(home);
                return Task.FromResult(CloneHome(_homes[home.Id]));
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
                Residents = h.Residents?.Select(CloneResident).ToList() ?? new List<Resident>(),
                AssociatedUsers = h.AssociatedUsers?.Select(CloneAssociatedUser).ToList() ?? new List<HomeAssociatedUser>()
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
                VisibleInDirectory = p.VisibleInDirectory
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
                SunshineCommitteeEmailOptedIn = e.SunshineCommitteeEmailOptedIn
            };
        }

        private static Resident CloneResident(Resident r)
        {
            if (r == null)
            {
                return null;
            }

            return new Resident
            {
                GivenName = r.GivenName,
                Surname = r.Surname,
                YearOfBirth = r.YearOfBirth,
                CollegeName = r.CollegeName,
                ResidentType = r.ResidentType,
                EmailAddresses = r.EmailAddresses?.Select(CloneEmailAddress).ToList() ?? new List<EmailAddress>(),
                PhoneNumbers = r.PhoneNumbers?.Select(ClonePhoneNumber).ToList() ?? new List<PhoneNumber>()
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
                IdentityProvider = u.IdentityProvider
            };
        }
    }
}
