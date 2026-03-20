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
                return Task.FromResult(_homes.Values.ToList());
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
                return Task.FromResult(_homes[home.Id]);
            }
        }

        private static Home CloneHome(Home h)
        {
            // Shallow clone sufficient for in-memory mock; avoid shared references mutating seed unexpectedly.
            return new Home
            {
                Id = h.Id,
                StreetNumber = h.StreetNumber,
                StreetName = h.StreetName,
                PhoneNumber = h.PhoneNumber,
                EmailAddress = h.EmailAddress,
                Residents = h.Residents?.ToList() ?? new List<Resident>(),
                AssociatedUsers = h.AssociatedUsers?.ToList() ?? new List<HomeAssociatedUser>()
            };
        }
    }
}
