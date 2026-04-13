using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Web.Models;
using Web.Services.Repositories;

namespace Web.Services
{
    public interface IEventSignupConversionService
    {
        /// <summary>
        /// Converts any user-based event signups for <paramref name="userUniqueId"/> into home-based
        /// signups for the specified home. If the home already has a signup for an event, the
        /// user-based signup is removed (the home signup takes precedence).
        /// </summary>
        /// <returns>The number of events whose signups were modified.</returns>
        Task<int> ConvertUserSignupsToHomeAsync(string userUniqueId, Guid homeId, string homeAddress);

        /// <summary>
        /// One-time migration: scans all events for user-based signups, looks up each user,
        /// and converts signups to home-based when the user now owns a home.
        /// </summary>
        Task<EventSignupMigrationResult> MigrateAllUserSignupsAsync(
            IUserRepository userRepository,
            IHomeRepository homeRepository
        );
    }

    public class EventSignupMigrationResult
    {
        public int EventsScanned { get; set; }
        public int SignupsConverted { get; set; }
        public int SignupsRemoved { get; set; }
        public int SignupsSkipped { get; set; }
        public CappedDetailList Details { get; set; } = new();
    }

    /// <summary>
    /// A list that stops accepting new entries after a fixed limit, preventing unbounded growth.
    /// </summary>
    public class CappedDetailList : List<string>
    {
        private const int MaxEntries = 200;
        private bool _capped;

        public new void Add(string item)
        {
            if (Count < MaxEntries)
            {
                base.Add(item);
                return;
            }

            if (!_capped)
            {
                base.Add($"… {MaxEntries} detail limit reached; remaining entries omitted.");
                _capped = true;
            }
        }
    }

    public class EventSignupConversionService : IEventSignupConversionService
    {
        private readonly ICommunityEventRepository _eventRepository;

        public EventSignupConversionService(ICommunityEventRepository eventRepository)
        {
            _eventRepository = eventRepository;
        }

        public async Task<int> ConvertUserSignupsToHomeAsync(string userUniqueId, Guid homeId, string homeAddress)
        {
            if (string.IsNullOrEmpty(userUniqueId) || homeId == Guid.Empty)
            {
                return 0;
            }

            var candidates = (await _eventRepository.GetEventsWithUserSignupAsync(userUniqueId)).ToList();

            var converted = 0;
            const int maxRetries = 3;

            foreach (var candidate in candidates)
            {
                for (var attempt = 0; attempt <= maxRetries; attempt++)
                {
                    var read = await _eventRepository.ReadAsync(candidate.Id);
                    if (read == null)
                    {
                        break;
                    }

                    var signups = read.Event.Signups ?? new List<EventSignup>();
                    var userSignups = signups
                        .Where(s => s.HomeId == Guid.Empty && s.UserUniqueId == userUniqueId)
                        .ToList();

                    if (userSignups.Count == 0)
                    {
                        break; // Already converted or removed by another process.
                    }

                    var homeAlreadySignedUp = signups.Any(s => s.HomeId == homeId);
                    var first = true;
                    foreach (var userSignup in userSignups)
                    {
                        if (!first || homeAlreadySignedUp)
                        {
                            // Remove duplicates and conflicts.
                            signups.Remove(userSignup);
                        }
                        else
                        {
                            userSignup.HomeId = homeId;
                            userSignup.HomeAddress = homeAddress;
                            userSignup.UserUniqueId = null;
                        }

                        first = false;
                    }

                    try
                    {
                        await _eventRepository.ReplaceAsync(read.Event, read.ETag);
                        converted++;
                        break;
                    }
                    catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
                    {
                        if (attempt == maxRetries) break; // Give up on this event after retries.
                    }
                    catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                    {
                        break; // Event was deleted.
                    }
                }
            }

            return converted;
        }

        public async Task<EventSignupMigrationResult> MigrateAllUserSignupsAsync(
            IUserRepository userRepository,
            IHomeRepository homeRepository
        )
        {
            var result = new EventSignupMigrationResult();

            var allEvents = await _eventRepository.GetAllAsync();
            result.EventsScanned = allEvents.Count;

            // Collect distinct user IDs from user-based signups across all events.
            var userSignupEntries = allEvents
                .Where(e => e.Signups != null)
                .SelectMany(e => e.Signups
                    .Where(s => s.HomeId == Guid.Empty && !string.IsNullOrEmpty(s.UserUniqueId))
                    .Select(s => new { EventId = e.Id, s.UserUniqueId })
                )
                .ToList();

            if (userSignupEntries.Count == 0)
            {
                result.Details.Add("No user-based signups found.");
                return result;
            }

            var distinctUserIds = userSignupEntries.Select(e => e.UserUniqueId).Distinct().ToList();
            result.Details.Add($"Found {userSignupEntries.Count} user-based signup(s) across {distinctUserIds.Count} user(s).");

            // Batch-load all users and resolve their primary homes.
            var allUsers = await userRepository.GetAllAsync();
            var userLookup = allUsers
                .Where(u => u.UniqueId != null)
                .ToDictionary(u => u.UniqueId, StringComparer.Ordinal);

            var userHomeMap = new Dictionary<string, (Guid HomeId, string HomeAddress)>();
            var homeIdsToFetch = new List<Guid>();
            var userPrimaryHomeMap = new Dictionary<string, Guid>();

            foreach (var uid in distinctUserIds)
            {
                if (!userLookup.TryGetValue(uid, out var user)
                    || user.OwnedHomeIds == null
                    || user.OwnedHomeIds.Count == 0)
                {
                    continue;
                }

                var primaryHomeId = user.OwnedHomeIds[0];
                userPrimaryHomeMap[uid] = primaryHomeId;
                homeIdsToFetch.Add(primaryHomeId);
            }

            var homes = await homeRepository.GetByIdsAsync(homeIdsToFetch.Distinct().ToList());
            var homeLookup = homes.ToDictionary(h => h.Id);

            foreach (var (uid, primaryHomeId) in userPrimaryHomeMap)
            {
                if (homeLookup.TryGetValue(primaryHomeId, out var home))
                {
                    userHomeMap[uid] = (home.Id, $"{home.StreetNumber} {home.StreetName}".Trim());
                }
            }

            // Process each event that has user-based signups eligible for conversion.
            result.SignupsSkipped = userSignupEntries.Count(e => !userHomeMap.ContainsKey(e.UserUniqueId));
            var affectedEventIds = userSignupEntries
                .Where(e => userHomeMap.ContainsKey(e.UserUniqueId))
                .Select(e => e.EventId)
                .Distinct()
                .ToList();

            const int maxRetries = 3;
            foreach (var eventId in affectedEventIds)
            {
                for (var attempt = 0; attempt <= maxRetries; attempt++)
                {
                    var read = await _eventRepository.ReadAsync(eventId);
                    if (read == null)
                    {
                        break;
                    }

                    var signups = read.Event.Signups ?? new List<EventSignup>();
                    var modified = false;

                    foreach (var signup in signups.ToList())
                    {
                        if (signup.HomeId != Guid.Empty || string.IsNullOrEmpty(signup.UserUniqueId))
                        {
                            continue;
                        }

                        if (!userHomeMap.TryGetValue(signup.UserUniqueId, out var homeInfo))
                        {
                            continue;
                        }

                        var homeAlreadySignedUp = signups.Any(s => s.HomeId == homeInfo.HomeId);
                        if (homeAlreadySignedUp)
                        {
                            signups.Remove(signup);
                            result.SignupsRemoved++;
                            result.Details.Add(
                                $"Removed duplicate user signup for '{signup.UserUniqueId}' from '{read.Event.Title}' " +
                                $"(home {homeInfo.HomeAddress} already signed up)."
                            );
                        }
                        else
                        {
                            signup.HomeId = homeInfo.HomeId;
                            signup.HomeAddress = homeInfo.HomeAddress;
                            signup.UserUniqueId = null;
                            result.SignupsConverted++;
                            result.Details.Add(
                                $"Converted signup to home-based for '{homeInfo.HomeAddress}' in '{read.Event.Title}'."
                            );
                        }

                        modified = true;
                    }

                    if (!modified)
                    {
                        break;
                    }

                    try
                    {
                        await _eventRepository.ReplaceAsync(read.Event, read.ETag);
                        break;
                    }
                    catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
                    {
                        if (attempt == maxRetries)
                        {
                            result.Details.Add($"Failed to save '{read.Event.Title}' after {maxRetries} retries (concurrency conflict).");
                        }
                    }
                    catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                    {
                        result.Details.Add($"Event '{read.Event.Title}' was deleted during migration.");
                        break;
                    }
                }
            }

            var skippedUsers = distinctUserIds.Count - userHomeMap.Count;
            if (skippedUsers > 0)
            {
                result.Details.Add($"{skippedUsers} user(s) could not be resolved to a home (no home association or home record missing) — their signups were left as-is.");
            }

            return result;
        }
    }
}
