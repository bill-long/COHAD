#nullable enable
using System;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json.Linq;
using Web.Models;
using CosmosContainer = Microsoft.Azure.Cosmos.Container;
using CosmosPartitionKey = Microsoft.Azure.Cosmos.PartitionKey;

namespace Web.Services.Repositories
{
    /// <summary>
    /// Thrown when a link is created with an id that already exists. The id is random, so this is a
    /// collision rather than a caller error, and the caller's response is to generate a new one -
    /// see <c>IUnsubscribeLinkIssuer</c>.
    /// <para>
    /// Distinct from <see cref="ConcurrencyConflictException"/> on purpose: that one means "someone
    /// else wrote the record you were holding, re-read and retry", and retrying this the same way
    /// would re-issue the same colliding id forever.
    /// </para>
    /// </summary>
    public class DuplicateUnsubscribeLinkIdException : Exception
    {
        public DuplicateUnsubscribeLinkIdException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public interface IUnsubscribeLinkRepository
    {
        /// <summary>
        /// Creates the link, populating its <see cref="UnsubscribeLink.ETag"/> from the response.
        /// Throws <see cref="DuplicateUnsubscribeLinkIdException"/> if the id is already taken.
        /// </summary>
        Task AddAsync(UnsubscribeLink link);

        /// <summary>Returns the link with the given id, or null if there is none.</summary>
        Task<UnsubscribeLink?> GetByIdAsync(string id);
    }

    public class CosmosUnsubscribeLinkRepository : IUnsubscribeLinkRepository
    {
        private readonly CosmosContainer _container;

        public CosmosUnsubscribeLinkRepository(CosmosContainer container)
        {
            _container = container;
        }

        public async Task AddAsync(UnsubscribeLink link)
        {
            if (string.IsNullOrWhiteSpace(link.Id))
                throw new ArgumentException("UnsubscribeLink.Id must be set.", nameof(link));

            try
            {
                var response = await _container.CreateItemAsync(ToDocument(link), CosmosPartitionKey.None);
                link.ETag = response.Headers.ETag;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
            {
                throw new DuplicateUnsubscribeLinkIdException("An unsubscribe link with this id already exists.", ex);
            }
        }

        public async Task<UnsubscribeLink?> GetByIdAsync(string id)
        {
            // Guarded before the read rather than after: an empty id is not a Cosmos-legal document
            // id, so ReadItemAsync would surface it as an argument fault rather than the 404 the
            // caller is prepared for. An absent credential is a rejection, not a server error.
            if (string.IsNullOrWhiteSpace(id))
                return null;

            try
            {
                var response = await _container.ReadItemAsync<JObject>(id, CosmosPartitionKey.None);
                var link = ToLink(response.Resource);
                link.ETag = response.Headers.ETag;
                return link;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound && ex.SubStatusCode == 0)
            {
                // Only a genuinely missing item maps to "no such link". A 404 with a non-zero
                // sub-status means the container or database is missing, and swallowing that would
                // turn a provisioning mistake into every recipient's link silently failing to
                // resolve - indistinguishable, in the logs, from the mangled-link incident this work
                // exists to diagnose.
                return null;
            }
        }

        private static UnsubscribeLink ToLink(JObject doc)
        {
            return new UnsubscribeLink
            {
                Id = doc.Value<string>("id") ?? string.Empty,
                HomeId = doc.Value<Guid?>("HomeId") ?? Guid.Empty,
                Email = doc.Value<string>("Email") ?? string.Empty,
                IssuedUtc = doc["IssuedUtc"]?.ToObject<DateTime>() ?? DateTime.MinValue,
                ETag = doc.Value<string>("_etag"),
            };
        }

        private static JObject ToDocument(UnsubscribeLink link)
        {
            return new JObject
            {
                ["id"] = link.Id,
                ["Discriminator"] = "UnsubscribeLink",
                ["HomeId"] = link.HomeId,
                ["Email"] = link.Email,
                ["IssuedUtc"] = JToken.FromObject(link.IssuedUtc),
            };
        }
    }
}
