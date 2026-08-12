using System.Net;
using Microsoft.Azure.Cosmos;

namespace Web.Services.Cosmos
{
    /// <summary>
    /// The one definition of "this CosmosException means the item does not exist": 404 with
    /// sub-status 0. Any other 404 (e.g. a missing or misconfigured container) is an
    /// infrastructure problem that must surface rather than read as "no document".
    /// </summary>
    internal static class CosmosNotFound
    {
        internal static bool IsItemNotFound(CosmosException ex) =>
            ex.StatusCode == HttpStatusCode.NotFound && ex.SubStatusCode == 0;
    }
}
