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

        /// <summary>
        /// Internal so a test can round-trip a document through real JSON text.
        /// <para>
        /// That round trip is the point: the Mock repository stores live objects and never
        /// serialises, so it cannot observe a mapping that only breaks once Cosmos has written and
        /// re-read the document. A read that threw on every stored row shipped past a green suite
        /// exactly that way.
        /// </para>
        /// </summary>
        internal static UnsubscribeLink ToLink(JObject doc)
        {
            // Parsed from the string form, matching every other Guid read in this codebase. Cosmos
            // stores a Guid as a JSON string, and `Value<Guid?>` routes a String token through
            // Convert.ChangeType, which throws InvalidCastException rather than returning null - so
            // the read failed on every stored row while a suite backed by the Mock stayed green.
            // A malformed value resolves to Guid.Empty, which the resolver rejects with a named
            // reason instead of a 500 the diagnostics middleware cannot even see.
            Guid.TryParse(doc.Value<string>("HomeId"), out var homeId);

            return new UnsubscribeLink
            {
                Id = doc.Value<string>("id") ?? string.Empty,
                HomeId = homeId,
                Email = doc.Value<string>("Email") ?? string.Empty,
                IssuedUtc = doc["IssuedUtc"]?.ToObject<DateTime>() ?? DateTime.MinValue,
                ETag = doc.Value<string>("_etag"),
            };
        }

        /// <summary>Internal for the round-trip test described on <see cref="ToLink"/>.</summary>
        internal static JObject ToDocument(UnsubscribeLink link)
        {
            return new JObject
            {
                ["id"] = link.Id,
                ["Discriminator"] = "UnsubscribeLink",
                // ToString("D") like every other mapper here, so the stored format is stated rather
                // than left to the serialiser. Writing the Guid directly is what paired with a
                // Value<Guid?> read to throw on every stored row.
                ["HomeId"] = link.HomeId.ToString("D"),
                ["Email"] = link.Email,
                ["IssuedUtc"] = JToken.FromObject(link.IssuedUtc),
                // Per-document retention, so a row holding an address deletes itself on a horizon
                // this code controls rather than only on the container's out-of-band setting.
                ["ttl"] = UnsubscribeLink.RetentionSeconds,
            };
        }
    }
}
