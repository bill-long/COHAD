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
                        UserUniqueId = MockDataConstants.AdminUniqueId,
                        UserDisplayName = "Mock Resident",
                        UserEmail = "mock@cohad.local",
                        Adults = 2,
                        Children = 1,
                        AdultNames = new List<string> { "Mock Resident", "Guest One" },
                        ChildNames = new List<string> { "Kid One" },
                        SignedUpUtc = now.AddHours(-12)
                    }
                }
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
                Signups = new List<EventSignup>()
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

                if (Guid.TryParse(segment, out var guid))
                {
                    return GetByIdAsync(guid);
                }

                var match = _events.Values.FirstOrDefault(e =>
                    string.Equals(EventUrlSlug.ResolveUrlSegment(e), segment, StringComparison.OrdinalIgnoreCase));
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
                return Task.FromResult(new CommunityEventReadResult
                {
                    Event = CloneEvent(e),
                    ETag = _etags[id]
                });
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
                    throw new CosmosException("Precondition failed", HttpStatusCode.PreconditionFailed, 0, string.Empty, 0);
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
                Title = communityEvent.Title,
                Description = communityEvent.Description,
                StartUtc = communityEvent.StartUtc,
                AllowSignups = communityEvent.AllowSignups,
                PromoMediaBlobPath = communityEvent.PromoMediaBlobPath,
                PromoMediaDisplayName = communityEvent.PromoMediaDisplayName,
                PromoMediaContentType = communityEvent.PromoMediaContentType,
                PromoMediaSizeBytes = communityEvent.PromoMediaSizeBytes,
                CreatedByUniqueId = communityEvent.CreatedByUniqueId,
                ModifiedByUniqueId = communityEvent.ModifiedByUniqueId,
                CreatedUtc = communityEvent.CreatedUtc,
                ModifiedUtc = communityEvent.ModifiedUtc,
                Signups = communityEvent.Signups?.Select(s => new EventSignup
                {
                    UserUniqueId = s.UserUniqueId,
                    UserDisplayName = s.UserDisplayName,
                    UserEmail = s.UserEmail,
                    Adults = s.Adults,
                    Children = s.Children,
                    AdultNames = s.AdultNames?.ToList() ?? new List<string>(),
                    ChildNames = s.ChildNames?.ToList() ?? new List<string>(),
                    SignedUpUtc = s.SignedUpUtc
                }).ToList() ?? new List<EventSignup>()
            };
        }
    }
}
