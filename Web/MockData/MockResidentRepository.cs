using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Web.Models;
using Web.Services.Repositories;

namespace Web.MockData
{
    public sealed class MockResidentRepository : IResidentRepository
    {
        private readonly Dictionary<Guid, Resident> _residents = new();

        public MockResidentRepository()
        {
            Seed();
        }

        public Task<List<Resident>> GetAllAsync()
        {
            lock (_residents)
            {
                return Task.FromResult(_residents.Values.Select(Clone).ToList());
            }
        }

        public Task<Resident> GetByIdAsync(Guid id)
        {
            lock (_residents)
            {
                return Task.FromResult(_residents.TryGetValue(id, out var r) ? Clone(r) : null);
            }
        }

        public Task<List<Resident>> GetByIdsAsync(IEnumerable<Guid> ids)
        {
            lock (_residents)
            {
                var set = new HashSet<Guid>(ids);
                return Task.FromResult(_residents.Values.Where(r => set.Contains(r.Id)).Select(Clone).ToList());
            }
        }

        public Task<List<Resident>> GetByHomeIdAsync(Guid homeId)
        {
            lock (_residents)
            {
                return Task.FromResult(_residents.Values.Where(r => r.HomeId == homeId).Select(Clone).ToList());
            }
        }

        public Task<List<Resident>> GetByHomeIdsAsync(IEnumerable<Guid> homeIds)
        {
            var idSet = homeIds?.ToHashSet() ?? new HashSet<Guid>();
            lock (_residents)
            {
                return Task.FromResult(_residents.Values.Where(r => idSet.Contains(r.HomeId)).Select(Clone).ToList());
            }
        }

        public Task<Resident> UpsertAsync(Resident resident)
        {
            lock (_residents)
            {
                _residents[resident.Id] = Clone(resident);
                return Task.FromResult(Clone(_residents[resident.Id]));
            }
        }

        public Task DeleteAsync(Guid id)
        {
            lock (_residents)
            {
                _residents.Remove(id);
            }

            return Task.CompletedTask;
        }

        public Task DeleteByHomeIdAsync(Guid homeId)
        {
            lock (_residents)
            {
                var toRemove = _residents.Values.Where(r => r.HomeId == homeId).Select(r => r.Id).ToList();
                foreach (var id in toRemove)
                {
                    _residents.Remove(id);
                }
            }

            return Task.CompletedTask;
        }

        private void Seed()
        {
            var residents = new List<Resident>
            {
                new Resident
                {
                    Id = MockDataConstants.MockResident1Id,
                    HomeId = MockDataConstants.SampleHomeId,
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
                            BoardEmailOptedIn = true,
                            WelcomeEmailOptedIn = false,
                            GardenClubEmailOptedIn = false,
                            SocialCommitteeEmailOptedIn = false,
                            SunshineCommitteeEmailOptedIn = false,
                        },
                    },
                    PhoneNumbers = new List<PhoneNumber>(),
                },
                new Resident
                {
                    Id = MockDataConstants.TaylorResident1Id,
                    HomeId = MockDataConstants.SecondSampleHomeId,
                    GivenName = "Taylor",
                    Surname = "Neighbor",
                    YearOfBirth = 1990,
                    CollegeName = "",
                    ResidentType = Resident.Type.Homeowner,
                    EmailAddresses = new List<EmailAddress>
                    {
                        new EmailAddress
                        {
                            Address = "taylor@cohad.local",
                            VisibleInDirectory = true,
                            BoardEmailOptedIn = true,
                            WelcomeEmailOptedIn = false,
                            GardenClubEmailOptedIn = false,
                            SocialCommitteeEmailOptedIn = false,
                            SunshineCommitteeEmailOptedIn = false,
                        },
                    },
                    PhoneNumbers = new List<PhoneNumber>(),
                },
            };

            foreach (var r in residents)
                _residents[r.Id] = r;
        }

        private static Resident Clone(Resident r)
        {
            if (r == null)
                return null;
            return new Resident
            {
                Id = r.Id,
                HomeId = r.HomeId,
                GivenName = r.GivenName,
                Surname = r.Surname,
                YearOfBirth = r.YearOfBirth,
                CollegeName = r.CollegeName,
                ResidentType = r.ResidentType,
                EmailAddresses = r.EmailAddresses?.Select(CloneEmail).ToList() ?? new List<EmailAddress>(),
                PhoneNumbers = r.PhoneNumbers?.Select(ClonePhone).ToList() ?? new List<PhoneNumber>(),
            };
        }

        private static EmailAddress CloneEmail(EmailAddress e)
        {
            if (e == null)
                return null;
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

        private static PhoneNumber ClonePhone(PhoneNumber p)
        {
            if (p == null)
                return null;
            return new PhoneNumber
            {
                AreaCode = p.AreaCode,
                Prefix = p.Prefix,
                LineNumber = p.LineNumber,
                Type = p.Type,
                VisibleInDirectory = p.VisibleInDirectory,
            };
        }
    }
}
