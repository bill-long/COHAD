using Web.Models;

namespace Web.Services.Repositories
{
    /// <summary>
    /// Result of a point read of a community event, including the Cosmos <c>ETag</c> for optimistic concurrency.
    /// </summary>
    public sealed class CommunityEventReadResult
    {
        public CommunityEvent Event { get; init; }

        public string ETag { get; init; }
    }
}
