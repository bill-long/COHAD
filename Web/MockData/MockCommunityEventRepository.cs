using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Web.Models;
using Web.Services.Repositories;

namespace Web.MockData
{
    public sealed class MockCommunityEventRepository : ICommunityEventRepository
    {
        private readonly Dictionary<Guid, CommunityEvent> _events = new();
        private readonly Dictionary<Guid, string> _etags = new();

        public MockCommunityEventRepository()
        {
            var now = DateTime.UtcNow;
            var kickoffId = Guid.Parse("4bb5ce5f-6324-4c17-9a28-03b6f0a09afb");
            _events[kickoffId] = new CommunityEvent
            {
                Id = kickoffId,
                Title = "Spring Neighborhood Kickoff",
                Description = "Join neighbors at the clubhouse lawn for coffee, snacks, and spring announcements.",
                StartUtc = now.Date.AddDays(5).AddHours(16),
                AllowSignups = true,
                CreatedByUniqueId = MockDataConstants.AdminUniqueId,
                ModifiedByUniqueId = MockDataConstants.AdminUniqueId,
                CreatedUtc = now.AddDays(-1),
                ModifiedUtc = now.AddDays(-1),
                Signups = new List<EventSignup>
                {
                    new EventSignup
                    {
                        HomeId = MockDataConstants.SampleHomeId,
                        HomeAddress = "123 Mock Lane",
                        UserDisplayName = "Mock Resident",
                        UserEmail = "mock@cohad.local",
                        Adults = 2,
                        Children = 1,
                        AdultNames = new List<string> { "Mock Resident", "Guest One" },
                        ChildNames = new List<string> { "Kid One" },
                        SignedUpUtc = now.AddHours(-12),
                    },
                },
            };

            var garageSaleId = Guid.Parse("a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d");
            _events[garageSaleId] = new CommunityEvent
            {
                Id = garageSaleId,
                PublicSlug = $"{now.Year}-community-garage-sale",
                Title = "Community Garage Sale",
                Description =
                    "Set up in your driveway and sell your stuff! Sign up so we know which households are participating.",
                StartUtc = now.Date.AddDays(10).AddHours(8),
                AllowSignups = true,
                SignupMode = EventSignupMode.HouseholdOnly,
                CreatedByUniqueId = MockDataConstants.AdminUniqueId,
                ModifiedByUniqueId = MockDataConstants.AdminUniqueId,
                CreatedUtc = now.AddDays(-1),
                ModifiedUtc = now.AddDays(-1),
                Signups = new List<EventSignup>
                {
                    new EventSignup
                    {
                        HomeId = MockDataConstants.SampleHomeId,
                        HomeAddress = "123 Mock Lane",
                        UserDisplayName = "Mock Resident",
                        UserEmail = "mock@cohad.local",
                        Adults = 0,
                        Children = 0,
                        SignedUpUtc = now.AddHours(-6),
                    },
                },
            };

            var eggHuntId = Guid.Parse("b2c3d4e5-f6a7-4b8c-9d0e-1f2a3b4c5d6e");
            _events[eggHuntId] = new CommunityEvent
            {
                Id = eggHuntId,
                PublicSlug = $"{now.Year}-easter-egg-hunt",
                Title = "Easter Egg Hunt",
                Description =
                    "Bring the kids for a neighborhood egg hunt at the clubhouse! Sign up with the number of children attending.",
                StartUtc = now.Date.AddDays(20).AddHours(10),
                AllowSignups = true,
                SignupMode = EventSignupMode.ChildrenOnly,
                CreatedByUniqueId = MockDataConstants.AdminUniqueId,
                ModifiedByUniqueId = MockDataConstants.AdminUniqueId,
                CreatedUtc = now.AddDays(-1),
                ModifiedUtc = now.AddDays(-1),
                Signups = new List<EventSignup>
                {
                    new EventSignup
                    {
                        HomeId = MockDataConstants.SecondSampleHomeId,
                        HomeAddress = "456 Test Court",
                        UserDisplayName = "Taylor Resident",
                        UserEmail = "taylor@cohad.local",
                        Adults = 0,
                        Children = 3,
                        ChildNames = new List<string> { "Emma", "Liam", "Sophia" },
                        SignedUpUtc = now.AddHours(-3),
                    },
                },
            };

            var boardId = Guid.Parse("f9ec5d08-cf7e-4e77-9583-57f0a56f380b");
            _events[boardId] = new CommunityEvent
            {
                Id = boardId,
                Title = "Board Q&A Night",
                Description = "Open forum with the board to discuss neighborhood priorities.",
                StartUtc = now.Date.AddDays(14).AddHours(18),
                AllowSignups = false,
                CreatedByUniqueId = MockDataConstants.AdminUniqueId,
                ModifiedByUniqueId = MockDataConstants.AdminUniqueId,
                CreatedUtc = now.AddDays(-2),
                ModifiedUtc = now.AddDays(-2),
                Signups = new List<EventSignup>(),
            };

            var movieNightId = Guid.Parse("c3d4e5f6-a7b8-4c9d-0e1f-2a3b4c5d6e7f");
            _events[movieNightId] = new CommunityEvent
            {
                Id = movieNightId,
                PublicSlug = $"{now.Year}-outdoor-movie-night",
                Title = "Outdoor Movie Night",
                Description =
                    "Bring blankets and chairs for a movie under the stars at the clubhouse lawn! Sign up with how many people are coming.",
                StartUtc = now.Date.AddDays(25).AddHours(20),
                AllowSignups = true,
                SignupMode = EventSignupMode.PeopleOnly,
                CreatedByUniqueId = MockDataConstants.AdminUniqueId,
                ModifiedByUniqueId = MockDataConstants.AdminUniqueId,
                CreatedUtc = now.AddDays(-1),
                ModifiedUtc = now.AddDays(-1),
                Signups = new List<EventSignup>
                {
                    new EventSignup
                    {
                        HomeId = MockDataConstants.SecondSampleHomeId,
                        HomeAddress = "456 Test Court",
                        UserDisplayName = "Taylor Resident",
                        UserEmail = "taylor@cohad.local",
                        Adults = 5,
                        Children = 0,
                        AdultNames = new List<string> { "Taylor", "Jordan", "Sam", "Pat", "Riley" },
                        SignedUpUtc = now.AddHours(-2),
                    },
                },
            };
        }

        private void EnsureEtag(Guid id)
        {
            if (!_etags.ContainsKey(id))
            {
                _etags[id] = Guid.NewGuid().ToString("N");
            }
        }

        public Task<List<CommunityEvent>> GetAllAsync()
        {
            lock (_events)
            {
                return Task.FromResult(_events.Values.Select(CloneEvent).ToList());
            }
        }

        public Task<List<CommunityEvent>> GetWithStartUtcOnOrAfterAsync(DateTime minStartUtcInclusive)
        {
            lock (_events)
            {
                var list = _events.Values.Where(e => e.StartUtc >= minStartUtcInclusive).Select(CloneEvent).ToList();
                return Task.FromResult(list);
            }
        }

        public Task<CommunityEvent> GetByIdAsync(Guid id)
        {
            lock (_events)
            {
                return Task.FromResult(_events.TryGetValue(id, out var found) ? CloneEvent(found) : null);
            }
        }

        public Task<CommunityEvent> GetByRouteSegmentAsync(string segment)
        {
            lock (_events)
            {
                if (string.IsNullOrWhiteSpace(segment))
                {
                    return Task.FromResult<CommunityEvent>(null);
                }

                var normalizedSegment = segment.Trim().ToLowerInvariant();

                var match = _events.Values.FirstOrDefault(e =>
                {
                    var currentSlug = EventUrlSlug.ResolveUrlSegment(e);
                    return !string.IsNullOrWhiteSpace(currentSlug)
                        && currentSlug.Trim().ToLowerInvariant() == normalizedSegment;
                });

                match ??= _events.Values.FirstOrDefault(e =>
                    e.PreviousSlugs?.Any(s =>
                        !string.IsNullOrWhiteSpace(s) && s.Trim().ToLowerInvariant() == normalizedSegment
                    ) == true
                );

                return Task.FromResult(match == null ? null : CloneEvent(match));
            }
        }

        public Task<CommunityEventReadResult> ReadAsync(Guid id)
        {
            lock (_events)
            {
                if (!_events.TryGetValue(id, out var e))
                {
                    return Task.FromResult<CommunityEventReadResult>(null);
                }

                EnsureEtag(id);
                return Task.FromResult(new CommunityEventReadResult { Event = CloneEvent(e), ETag = _etags[id] });
            }
        }

        public Task<List<Guid>> GetEventIdsWithUserSignupAsync(string userUniqueId)
        {
            if (string.IsNullOrWhiteSpace(userUniqueId))
            {
                return Task.FromResult(new List<Guid>());
            }

            lock (_events)
            {
                var results = _events.Values
                    .Where(e =>
                        e.Signups != null
                        && e.Signups.Any(s => s.HomeId == Guid.Empty && s.UserUniqueId == userUniqueId)
                    )
                    .Select(e => e.Id)
                    .ToList();
                return Task.FromResult(results);
            }
        }

        public Task<CommunityEvent> UpsertAsync(CommunityEvent communityEvent)
        {
            lock (_events)
            {
                var copy = CloneEvent(communityEvent);
                if (copy.Id == Guid.Empty)
                {
                    copy.Id = Guid.NewGuid();
                }

                _events[copy.Id] = copy;
                _etags[copy.Id] = Guid.NewGuid().ToString("N");
                return Task.FromResult(CloneEvent(copy));
            }
        }

        public Task<CommunityEvent> ReplaceAsync(CommunityEvent communityEvent, string ifMatchEtag)
        {
            lock (_events)
            {
                var id = communityEvent.Id;
                if (!_events.ContainsKey(id))
                {
                    throw new CosmosException("Not found", HttpStatusCode.NotFound, 0, string.Empty, 0);
                }

                EnsureEtag(id);
                if (_etags[id] != ifMatchEtag)
                {
                    throw new CosmosException(
                        "Precondition failed",
                        HttpStatusCode.PreconditionFailed,
                        0,
                        string.Empty,
                        0
                    );
                }

                var copy = CloneEvent(communityEvent);
                _events[id] = copy;
                _etags[id] = Guid.NewGuid().ToString("N");
                return Task.FromResult(CloneEvent(copy));
            }
        }

        public Task DeleteAsync(Guid id)
        {
            lock (_events)
            {
                _events.Remove(id);
                _etags.Remove(id);
            }

            return Task.CompletedTask;
        }

        private static CommunityEvent CloneEvent(CommunityEvent communityEvent)
        {
            if (communityEvent == null)
            {
                return null;
            }

            return new CommunityEvent
            {
                Id = communityEvent.Id,
                PublicSlug = communityEvent.PublicSlug,
                PreviousSlugs = communityEvent.PreviousSlugs?.ToList() ?? new List<string>(),
                Title = communityEvent.Title,
                Description = communityEvent.Description,
                StartUtc = communityEvent.StartUtc,
                AllowSignups = communityEvent.AllowSignups,
                SignupMode = communityEvent.SignupMode,
                PromoMediaBlobPath = communityEvent.PromoMediaBlobPath,
                PromoMediaDisplayName = communityEvent.PromoMediaDisplayName,
                PromoMediaContentType = communityEvent.PromoMediaContentType,
                PromoMediaSizeBytes = communityEvent.PromoMediaSizeBytes,
                PromoMediaThumbBlobPath = communityEvent.PromoMediaThumbBlobPath,
                CreatedByUniqueId = communityEvent.CreatedByUniqueId,
                ModifiedByUniqueId = communityEvent.ModifiedByUniqueId,
                CreatedUtc = communityEvent.CreatedUtc,
                ModifiedUtc = communityEvent.ModifiedUtc,
                Signups =
                    communityEvent
                        .Signups?.Select(s => new EventSignup
                        {
                            HomeId = s.HomeId,
                            HomeAddress = s.HomeAddress,
                            UserUniqueId = s.UserUniqueId,
                            UserDisplayName = s.UserDisplayName,
                            UserEmail = s.UserEmail,
                            Adults = s.Adults,
                            Children = s.Children,
                            AdultNames = s.AdultNames?.ToList() ?? new List<string>(),
                            ChildNames = s.ChildNames?.ToList() ?? new List<string>(),
                            SignedUpUtc = s.SignedUpUtc,
                        })
                        .ToList()
                    ?? new List<EventSignup>(),
            };
        }
    }
}
