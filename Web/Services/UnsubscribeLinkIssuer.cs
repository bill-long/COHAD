#nullable enable
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Web.Models;
using Web.Services.Repositories;

namespace Web.Services
{
    public interface IUnsubscribeLinkIssuer
    {
        /// <summary>
        /// Issues a short link for the home and address, retrying on an id collision. Returns the
        /// stored link, whose <see cref="UnsubscribeLink.Id"/> is the credential to put in the URL.
        /// </summary>
        Task<UnsubscribeLink> IssueAsync(Guid homeId, string email);
    }

    /// <summary>
    /// Generates short link ids and stores them, treating a duplicate-id write as a collision and
    /// generating a new one.
    /// <para>
    /// The retry lives here rather than in the repository so the repository stays a thin mapping over
    /// the container and the Mock has one behaviour to reproduce - a 409 on a duplicate id - instead
    /// of a retry policy that could silently differ from the Cosmos one.
    /// </para>
    /// </summary>
    public class UnsubscribeLinkIssuer : IUnsubscribeLinkIssuer
    {
        /// <summary>
        /// Attempts before giving up. With 16 random bytes a genuine collision is not a thing that
        /// happens; three attempts exist so that if one ever does, or if something upstream starts
        /// producing non-random ids, it surfaces as a bounded failure rather than an unbounded loop.
        /// </summary>
        private const int MaxAttempts = 3;

        private readonly IUnsubscribeLinkRepository _repository;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger<UnsubscribeLinkIssuer> _logger;

        // The clock is injected rather than read from DateTime.UtcNow because the resolver's expiry
        // check reads an injected one, and a credential whose issue time and expiry come from two
        // different clocks has no testable boundary at all.
        public UnsubscribeLinkIssuer(
            IUnsubscribeLinkRepository repository,
            TimeProvider timeProvider,
            ILogger<UnsubscribeLinkIssuer> logger
        )
        {
            _repository = repository;
            _timeProvider = timeProvider;
            _logger = logger;
        }

        public async Task<UnsubscribeLink> IssueAsync(Guid homeId, string email)
        {
            // A blank address would produce a credential that authorises nothing resolvable, and the
            // resolver rejects it on redemption - so refuse to mint it in the first place rather than
            // writing a row whose only future is a logged rejection. Mirrors GenerateToken, which
            // refuses the same input.
            if (string.IsNullOrWhiteSpace(email))
                throw new ArgumentException("Email must not be empty.", nameof(email));

            DuplicateUnsubscribeLinkIdException lastCollision = null;

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                var link = new UnsubscribeLink
                {
                    Id = UnsubscribeLink.NewId(),
                    HomeId = homeId,
                    Email = email,
                    IssuedUtc = _timeProvider.GetUtcNow().UtcDateTime,
                };

                try
                {
                    await _repository.AddAsync(link);
                    return link;
                }
                catch (DuplicateUnsubscribeLinkIdException ex)
                {
                    // Caught on every attempt including the last, and the give-up below is what ends
                    // the loop. An `when (attempt < MaxAttempts)` filter here reads like it says the
                    // same thing but does not: the final collision would escape as a repository
                    // exception and the throw below would be unreachable - a handler that looks like
                    // it runs and never does.
                    lastCollision = ex;

                    // The id is never logged: it is the whole credential. Only that a collision
                    // happened, which at this id length is itself worth seeing.
                    _logger.LogWarning(
                        "Unsubscribe link id collision on attempt {Attempt} of {MaxAttempts}.",
                        attempt,
                        MaxAttempts
                    );
                }
            }

            throw new InvalidOperationException(
                $"Failed to issue an unsubscribe link after {MaxAttempts} id collisions.",
                lastCollision
            );
        }
    }
}
