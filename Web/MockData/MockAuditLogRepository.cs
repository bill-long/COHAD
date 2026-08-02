#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Web.Models;
using Web.Services.Repositories;

namespace Web.MockData
{
    public sealed class MockAuditLogRepository : IAuditLogRepository
    {
        private readonly List<NewAuditLogEntry> _entries = new();

        public MockAuditLogRepository()
        {
            SeedSampleEntries();
        }

        public Task AddAsync(NewAuditLogEntry entry)
        {
            lock (_entries)
            {
                _entries.Add(CloneEntry(entry));
            }

            return Task.CompletedTask;
        }

        public Task<List<NewAuditLogEntry>> GetAllAsync()
        {
            lock (_entries)
            {
                return Task.FromResult(_entries.Select(CloneEntry).ToList());
            }
        }

        public Task<AuditLogPage> GetPageAsync(int limit, string? continuationToken, string? searchQuery)
        {
            var pageSize = Math.Clamp(limit, 1, 200);
            if (!TryGetContinuationOffset(continuationToken, out var offset))
            {
                return Task.FromResult(
                    new AuditLogPage
                    {
                        Items = new List<NewAuditLogEntry>(),
                        ContinuationToken = null,
                        HasMore = false,
                    }
                );
            }

            var normalizedSearch = searchQuery?.Trim();

            lock (_entries)
            {
                IEnumerable<NewAuditLogEntry> query = _entries.OrderByDescending(e => e.Time);

                if (!string.IsNullOrWhiteSpace(normalizedSearch))
                {
                    query = query.Where(e => MatchesSearch(e, normalizedSearch));
                }

                var filtered = query.ToList();
                var pageItems = filtered.Skip(offset).Take(pageSize).Select(CloneEntry).ToList();
                var nextOffset = offset + pageItems.Count;
                var hasMore = nextOffset < filtered.Count;

                return Task.FromResult(
                    new AuditLogPage
                    {
                        Items = pageItems,
                        ContinuationToken = hasMore ? nextOffset.ToString() : null,
                        HasMore = hasMore,
                    }
                );
            }
        }

        /// <summary>
        /// Seeds enough history for the audit log page to be exercised locally - paging (the page size
        /// is 50), search, and the responsive layout, which only misbehaves once actions are long enough
        /// to wrap. Actions deliberately vary in length for that reason.
        /// Runs from the constructor, before the instance is shared, so it needs no lock.
        /// </summary>
        private void SeedSampleEntries()
        {
            var now = DateTime.UtcNow;

            var actions = new[]
            {
                "Updated home association",
                "Deleted resident phone number",
                "Approved a vendor review submitted by a resident",
                "Changed role assignments: added Administrator, removed WelcomeCommittee",
                "Uploaded document",
            };

            var subjects = new (string Id, string Name)[]
            {
                (MockDataConstants.SampleHomeId.ToString(), "123 Mock Lane"),
                (MockDataConstants.TaylorResident1Id.ToString(), "Taylor Neighbor"),
                (MockDataConstants.SecondSampleHomeId.ToString(), "456 Test Court"),
            };

            var actors = new (string Id, string Name)[]
            {
                (MockDataConstants.AdminUniqueId, "Mock Resident"),
                (MockDataConstants.SecondaryUserUniqueId, "Taylor Neighbor"),
            };

            for (var i = 0; i < 60; i++)
            {
                var subject = subjects[i % subjects.Length];
                var actor = actors[i % actors.Length];

                _entries.Add(
                    new NewAuditLogEntry
                    {
                        Id = Guid.NewGuid(),
                        Time = now.AddMinutes(-13 * i),
                        UserId = actor.Id,
                        UserDisplayName = actor.Name,
                        SubjectId = subject.Id,
                        SubjectName = subject.Name,
                        Action = actions[i % actions.Length],
                    }
                );
            }
        }

        private static bool MatchesSearch(NewAuditLogEntry entry, string searchQuery)
        {
            return ContainsIgnoreCase(entry?.UserDisplayName, searchQuery)
                || ContainsIgnoreCase(entry?.UserId, searchQuery)
                || ContainsIgnoreCase(entry?.SubjectName, searchQuery)
                || ContainsIgnoreCase(entry?.SubjectId, searchQuery)
                || ContainsIgnoreCase(entry?.Action, searchQuery);
        }

        private static bool ContainsIgnoreCase(string? value, string searchQuery)
        {
            if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(searchQuery))
            {
                return false;
            }

            return value.Contains(searchQuery, System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Aligns with <see cref="CosmosAuditLogRepository"/>: null/whitespace means first page; a non-empty
        /// token that is not a valid non-negative offset is treated as an invalid cursor (empty page, no more).
        /// </summary>
        private static bool TryGetContinuationOffset(string? continuationToken, out int offset)
        {
            offset = 0;
            if (string.IsNullOrWhiteSpace(continuationToken))
            {
                return true;
            }

            if (int.TryParse(continuationToken, out var parsed) && parsed >= 0)
            {
                offset = parsed;
                return true;
            }

            return false;
        }

        private static NewAuditLogEntry CloneEntry(NewAuditLogEntry e)
        {
            ArgumentNullException.ThrowIfNull(e);

            return new NewAuditLogEntry
            {
                Id = e.Id,
                Time = e.Time,
                UserId = e.UserId,
                UserDisplayName = e.UserDisplayName,
                SubjectId = e.SubjectId,
                SubjectName = e.SubjectName,
                Action = e.Action,
            };
        }
    }
}
