#nullable enable
using Microsoft.AspNetCore.Http;
using Web.Models;

namespace Web.Authorization
{
    /// <summary>
    /// Carries the user document an authorization handler already read to the endpoint that runs
    /// afterwards, so a controller needing the same user's roles does not repeat the point read.
    /// <para>
    /// Purely an optimization: a miss is always safe, and callers fall back to reading the user
    /// themselves. Nothing here grants access - authorization has already been decided by the time
    /// anything is stored.
    /// </para>
    /// </summary>
    internal static class AuthorizedUserCache
    {
        private const string ItemKey = "Web.Authorization.AuthorizedUser";

        /// <summary>
        /// Stores the user resolved while evaluating a policy for this request. Both arguments are
        /// nullable because handlers pass <c>context.Resource as HttpContext</c>, which is null
        /// whenever the policy guards something other than an HTTP endpoint (a SignalR hub, say).
        /// </summary>
        public static void Set(HttpContext? httpContext, User? user)
        {
            if (httpContext != null && user != null)
                httpContext.Items[ItemKey] = user;
        }

        /// <summary>
        /// The user resolved during authorization of this request, or null if no handler stored one
        /// (a different policy ran, the endpoint is anonymous, or the resource was not an HttpContext).
        /// Callers must handle the miss - it is never a guarantee that a user is present.
        /// </summary>
        public static User? Get(HttpContext? httpContext) =>
            httpContext?.Items.TryGetValue(ItemKey, out var cached) == true ? cached as User : null;
    }
}
