#nullable enable
using System.Security.Claims;
using System.Threading.Tasks;
using Web.Models;
using Web.Services.Repositories;

namespace Web.Services
{
    /// <summary>
    /// The user document for whoever is making the current request, read at most once per request.
    /// <para>
    /// Authorization already reads this document to evaluate a role policy, and most endpoints then
    /// read it again to do their own work - two point reads of the same Cosmos item per request.
    /// Registering this scoped and routing both through it collapses them to one, including on the
    /// endpoints no role policy guards (where nothing would have read it first).
    /// </para>
    /// </summary>
    public interface ICurrentUserAccessor
    {
        /// <summary>
        /// The caller's user document, or null when the token carries no usable identity claims or no
        /// user matches them. Repeat calls within one request return the first result.
        /// </summary>
        Task<User?> GetAsync(ClaimsPrincipal? principal);
    }

    /// <inheritdoc />
    internal sealed class CurrentUserAccessor : ICurrentUserAccessor
    {
        private readonly IUserRepository _userRepository;

        /// <summary>
        /// The in-flight or completed lookup, keyed by the identity it was made for. The task itself is
        /// cached rather than its result, so two callers that ask concurrently share one read instead
        /// of racing to start two.
        /// </summary>
        private string? _cachedUniqueId;
        private Task<User?>? _cachedLookup;

        public CurrentUserAccessor(IUserRepository userRepository)
        {
            _userRepository = userRepository;
        }

        public Task<User?> GetAsync(ClaimsPrincipal? principal)
        {
            var uniqueId = TryGetUniqueId(principal);
            if (uniqueId == null)
                return Task.FromResult<User?>(null);

            // Keyed rather than unconditional: a scope serves one caller in practice, but returning
            // some other principal's user would be an authorization bug, not a stale cache.
            if (_cachedLookup != null && _cachedUniqueId == uniqueId)
                return _cachedLookup;

            _cachedUniqueId = uniqueId;
            _cachedLookup = _userRepository.GetByUniqueIdAsync(uniqueId);
            return _cachedLookup;
        }

        /// <summary>
        /// The caller's unique id, or null when the required claims are absent.
        /// <see cref="User.GetUniqueIdFromClaims"/> throws in that case; an unauthenticated or
        /// partially-claimed request is a normal condition here, not an exceptional one.
        /// </summary>
        private static string? TryGetUniqueId(ClaimsPrincipal? principal)
        {
            if (principal?.Identity?.IsAuthenticated != true)
                return null;

            try
            {
                return User.GetUniqueIdFromClaims(principal.Claims);
            }
            catch (System.InvalidOperationException)
            {
                return null;
            }
        }
    }
}
